#nullable enable
using Godot;
using System;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UGC;
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "ContentBrowser" phase (runtime position 19). Creates the content browser panel (wired to mod.io
    /// when Inspector creds are configured) and owns its OnLoadMap handler — extract a .chimera.zip, copy the
    /// scenario + faction files into the project, then reload the scene. Publishes ctx.ContentBrowser.
    /// Behavior-identical to MainScene.SetupContentBrowser + HandleLoadMap.
    /// </summary>
    public sealed class ContentBrowserPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public ContentBrowserPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "ContentBrowser";

        public void Run()
        {
            // Create mod.io service if a game id is set (Inspector) AND a key is present in the secret store.
            // Story 8.1: the key comes from user://secrets/modio.key via ISecretStore, not a plaintext [Export].
            ModIoService? modIo = null;
            if (_ctx.Scene.ModIoGameId > 0 && _ctx.SecretStore.Has(SecretIds.ModIo))
            {
                modIo = new ModIoService(_ctx.Scene.ModIoGameId, _ctx.SecretStore.Get(SecretIds.ModIo));
                GD.Print($"[ContentBrowser] mod.io service created (game ID {_ctx.Scene.ModIoGameId}).");
            }

            _ctx.ContentBrowser = new ContentBrowserPanel();
            _ctx.Scene.AddChild(_ctx.ContentBrowser);
            // Story 9.8: hand the secret store so the Local-tab publish gate can verify a package's proof-of-play token.
            _ctx.ContentBrowser.Initialize("user://packages/", modIo, _ctx.SecretStore);
            _ctx.ContentBrowser.OnLoadMap += HandleLoadMap;

            GD.Print("[ContentBrowser] Initialized — press O in Edit mode to open. " +
                     "Drop .chimera.zip files into: " +
                     ProjectSettings.GlobalizePath("user://packages/"));
        }

        /// <summary>
        /// Called when the user clicks Load on a map package. Extracts the .chimera.zip to user://imported_maps/,
        /// copies the scenario + faction files into the project, points ScenarioPath at it, then reloads the scene.
        /// </summary>
        private void HandleLoadMap(string zipPath)
        {
            var manifest = ContentPackager.ReadManifest(zipPath);
            if (manifest == null)
            {
                GD.PrintErr($"[ContentBrowser] Cannot read manifest from '{zipPath}'.");
                return;
            }

            // Extract to user://imported_maps/<slug>/
            string extractDir = ProjectSettings.GlobalizePath($"user://imported_maps/{manifest.Id}/");
            try
            {
                var result = ContentPackager.Unpack(zipPath, extractDir);

                // Copy scenario into the project's scenarios resource directory.
                string destScenario = ProjectSettings.GlobalizePath(
                    $"res://resources/data/scenarios/{manifest.Id}.json");
                System.IO.File.Copy(result.ScenarioPath, destScenario, overwrite: true);

                // Copy any bundled faction files.
                foreach (var fp in result.FactionPaths)
                {
                    string destFaction = ProjectSettings.GlobalizePath(
                        $"res://resources/data/factions/{System.IO.Path.GetFileName(fp)}");
                    System.IO.File.Copy(fp, destFaction, overwrite: true);
                }

                // Update the Inspector property so the next scene reload picks this map.
                _ctx.Scene.ScenarioPath = $"res://resources/data/scenarios/{manifest.Id}.json";

                GD.Print($"[ContentBrowser] Loaded '{manifest.DisplayName}' → {_ctx.Scene.ScenarioPath}. Reloading scene...");

                // Reload the whole scene to reset all simulation state cleanly.
                _ctx.Scene.GetTree().ReloadCurrentScene();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ContentBrowser] Failed to load '{manifest.DisplayName}': {ex.Message}");

                // Re-open the browser so user can try again.
                _ctx.ContentBrowser.ToggleVisible();
            }
        }
    }
}
