#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 9.9 — a runtime map from a package's zip-relative logical asset id (e.g. "assets/heavy_tank.glb") to the
    /// ingested <see cref="Mesh"/>. A custom unit's <c>MeshPath</c> referencing that logical id resolves through here
    /// (see <see cref="MeshLoader.LoadFromGlb(string, Vector3, Color, AssetRegistry?)"/>); a <c>res://</c> path keeps
    /// the editor load path, and an absent id falls back to the box placeholder — so a missing/invalid asset never
    /// crashes rendering.
    ///
    /// One instance is owned by the render/session root (the bootstrap <c>SceneContext</c>) and shared between the
    /// content browser (which populates it on download via <see cref="IngestPackage"/>) and the unit render bridge
    /// (which consults it when loading per-type meshes).
    ///
    /// <para><b>DW-427 — key normalization.</b> Lookups used to be an exact, case-sensitive string match, so an id
    /// differing only in case/slash-direction/stray whitespace resolved on a case-insensitive filesystem but MISSED
    /// here, silently rendering the box placeholder. Both sides (<see cref="Register"/> and <see cref="TryGet"/>) now
    /// key through <see cref="MeshPathId.NormalizeKey"/>, and <see cref="RegisteredIds"/> exposes the ids as
    /// AUTHORED so an unresolved <c>MeshPath</c> can be diagnosed against them
    /// (<see cref="MeshPathId.DescribeRegistryMiss"/>).</para>
    /// </summary>
    public sealed class AssetRegistry
    {
        // A neutral placeholder box for a failed/invalid ingest; team colour is applied via material_override at render
        // time, so the registered mesh's own colour is irrelevant.
        private static readonly Vector3 FallbackSize  = new(0.6f, 1.2f, 0.6f);
        private static readonly Color   FallbackColor = new(0.6f, 0.6f, 0.6f);

        // Keyed by MeshPathId.NormalizeKey(logicalId) — see the class doc (DW-427). _authoredIds keeps the id as the
        // manifest authored it, keyed the same way, purely so a miss can be reported against readable ids.
        private readonly Dictionary<string, Mesh> _meshes = new();
        private readonly Dictionary<string, string> _authoredIds = new();

        /// <summary>Register (or replace) the mesh for a logical id. The key is normalized
        /// (<see cref="MeshPathId.NormalizeKey"/>), so two ids differing only in case/slash-direction/whitespace are
        /// the SAME asset — a re-register replaces, exactly as an identical id always did. A blank id is ignored
        /// (nothing could ever look it up).</summary>
        public void Register(string logicalId, Mesh mesh)
        {
            string key = MeshPathId.NormalizeKey(logicalId);
            if (key.Length == 0) return;

            if (_authoredIds.TryGetValue(key, out string? existing) && existing != logicalId)
                GD.Print($"[AssetRegistry] '{logicalId}' normalizes to the same asset key as the already-registered " +
                         $"'{existing}' — the later registration wins.");

            _meshes[key] = mesh;
            _authoredIds[key] = logicalId;
        }

        /// <summary>Look up the mesh for a logical id. Returns false (and null) if absent. Normalizes the id the same
        /// way <see cref="Register"/> did (DW-427).</summary>
        public bool TryGet(string logicalId, out Mesh mesh)
            => _meshes.TryGetValue(MeshPathId.NormalizeKey(logicalId), out mesh!);

        /// <summary>Number of registered assets (for diagnostics/inspection).</summary>
        public int Count => _meshes.Count;

        /// <summary>The registered logical ids AS AUTHORED (unnormalized), for the DW-427 unresolved-MeshPath
        /// diagnostic. Order is unspecified — the diagnostic sorts them itself.</summary>
        public IReadOnlyCollection<string> RegisteredIds => _authoredIds.Values;

        /// <summary>
        /// Ingest every allow-listed asset a package extracted under <paramref name="extractDir"/>/<c>assets/</c> and
        /// register each under its zip-relative logical id (the entries in <paramref name="assetFiles"/>, e.g.
        /// "assets/heavy_tank.glb" — the same values as <c>UnpackResult.Manifest.AssetFiles</c>). A non-allow-listed
        /// extension is skipped entirely (no scene load); a valid-extension asset is ingested via
        /// <see cref="RuntimeAssetIngest"/>, which registers a box placeholder on any invalid/unsafe/malformed asset.
        /// </summary>
        public void IngestPackage(string extractDir, IEnumerable<string> assetFiles)
        {
            if (assetFiles == null) return;

            foreach (var logicalId in assetFiles)
            {
                string name = System.IO.Path.GetFileName(logicalId);
                string ext = System.IO.Path.GetExtension(name);
                if (!AssetValidator.IsAllowedExtension(ext))
                {
                    GD.Print($"[AssetRegistry] Skipping non-allow-listed asset '{logicalId}'.");
                    continue;
                }

                string absPath = System.IO.Path.Combine(extractDir, "assets", name);
                Mesh mesh = RuntimeAssetIngest.Ingest(absPath, FallbackSize, FallbackColor);
                Register(logicalId, mesh);
            }
        }
    }
}
