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

        /// <summary>Story 11.1 / DW-462 — resolve a faction slot's team color from the Godot-free
        /// <see cref="TeamColorPalette"/> seam (channels pass through verbatim, so rendered colors are unchanged).
        /// The palette + the 1-based <see cref="Faction"/> → 0-based palette −1 shift were extracted to
        /// <c>Core/Bootstrap/TeamColorPalette.cs</c> so both are pinned by Tier-1 RGBA assertions
        /// (<c>TeamColorSlotMappingTests</c>) — this phase is excluded from the sim glob and the shift shipped
        /// inverted TWICE during 11.1 (Player1 red / Player2 green), each time caught only by human review.
        /// PATCH 7's single-source invariant still holds, one level deeper: the setup screen's swatches read the SAME
        /// seam (<c>SkirmishSetupOverlay</c> keys it by <c>SkirmishSetupToScenario.LaunchIndexBySlot</c>, DW-460), so
        /// the setup-time color cannot drift from the in-match team color.</summary>
        private static Color SlotColor(Faction faction) => TeamColorPalette.ColorForFaction(faction).ToColor();

        public void Run()
        {
            // Story 9.9 (review P1): populate the render/session AssetRegistry the bridges below read. The download-time
            // ingest (ContentBrowserPanel) populated a SceneContext registry that ReloadCurrentScene then discarded, so
            // the ingest that must actually render belongs HERE, on the load-to-play path, into the post-reload
            // registry. HandleLoadMap already integrity-verified + unpacked the package (assets included) to
            // user://imported_maps/<id>/; the reloaded scene identifies that map by its scenario path stem.
            IngestImportedAssets();

            // Story 11.1: per-slot-index color palette (honest 2→4 extension — no per-slot color data channel, so this
            // stays deterministic by faction slot index). Index 0/1 keep today's blue/red exactly so goldens and visual
            // continuity hold; index 2/3 give 3- and 4-player skirmishes distinct colors. Indexed by faction slot.
            Color p1Color = SlotColor(Faction.Player1); // Player 1 = blue
            Color p2Color = SlotColor(Faction.Player2); // Player 2 = red

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
        /// an assets/ subdir); a normal res:// scenario has no such dir, so this is a no-op.
        ///
        /// <para>DW-426 — ingest is driven by the manifest's integrity-verified <c>AssetFiles</c> list, NOT a raw scan
        /// of the on-disk assets/ dir: a scan would also pick up a stale/orphan .glb left by a prior same-Id import or
        /// a failed extraction — bytes THIS package's integrity check never covered. <c>Unpack</c> materializes
        /// <c>manifest.json</c> into the extract dir as its final step (only after every integrity check passed), so
        /// its presence is the "extraction completed + verified" seal; absent ⇒ nothing verifiable ⇒ skip (re-loading
        /// the package from the content browser re-materializes it). Each listed asset is decoded defensively — an
        /// invalid/unsafe file still falls back to the box placeholder inside
        /// <see cref="AssetRegistry.IngestPackage"/>.</para>
        /// </summary>
        private void IngestImportedAssets()
        {
            string scenarioPath = _ctx.Scene.ScenarioPath;
            if (string.IsNullOrEmpty(scenarioPath)) return;

            string mapId = System.IO.Path.GetFileNameWithoutExtension(scenarioPath);
            if (string.IsNullOrEmpty(mapId)) return;

            string importDir = ProjectSettings.GlobalizePath($"user://imported_maps/{mapId}/");
            if (!System.IO.Directory.Exists(importDir)) return;

            var manifest = ContentPackager.ReadExtractedManifest(importDir);
            if (manifest == null)
            {
                // Legacy (pre-DW-426) or partial extraction: no verified manifest seal, so nothing is ingested.
                if (System.IO.Directory.Exists(System.IO.Path.Combine(importDir, "assets")))
                    GD.Print($"[FactionVisuals] {importDir} has an assets/ dir but no verified manifest.json " +
                             "(pre-DW-426 or partial extraction) — skipping asset ingest; re-load the package " +
                             "from the content browser to re-materialize it.");
                return;
            }

            if (manifest.AssetFiles == null || manifest.AssetFiles.Count == 0) return;

            _ctx.AssetRegistry.IngestPackage(importDir, manifest.AssetFiles);
            GD.Print($"[FactionVisuals] Ingested {_ctx.AssetRegistry.Count} manifest-listed custom asset(s) " +
                     $"from {importDir}.");
        }
    }
}
