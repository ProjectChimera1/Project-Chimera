#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// DW-427 — the single source of truth for what an authored <c>mesh_path</c> (<see cref="UnitDefinition.MeshPath"/>
    /// / <see cref="BuildingDefinition.MeshPath"/>) MEANS, and for the diagnostic emitted when one fails to resolve.
    ///
    /// <para><b>The convention (two forms, no third).</b>
    ///   1. A <c>res://</c> PROJECT RESOURCE PATH — e.g. <c>"res://assets/models/factions/alpha/crucible_hall.glb"</c>.
    ///      Shipped/editor content uses this form; it is loaded through Godot's resource loader and its on-disk
    ///      existence is linted at the authoring Save edge by <see cref="MeshAssetLint"/>.
    ///   2. A PACKAGE ASSET ID — the zip-relative logical id of a bundled custom asset, EXACTLY as it appears in the
    ///      package manifest's <c>asset_files</c> list (e.g. <c>"assets/heavy_tank.glb"</c>, see
    ///      <see cref="ContentPackageManifest.AssetFiles"/>). It is resolved at render time against the ingested
    ///      asset registry, never against the filesystem — a downloaded package's GLB does not live under
    ///      <c>res://</c> at all.
    /// Anything else (a bare filename, an absolute OS path, a stale id) resolves to NEITHER and renders the grey box
    /// placeholder — which, before this class existed, happened silently with nothing for the author to debug.</para>
    ///
    /// <para><b>Key normalization (<see cref="NormalizeKey"/>).</b> The registry lookup was an exact, case-sensitive
    /// string match, so on Windows (a case-insensitive filesystem) an id whose CASE differed resolved on disk but
    /// missed in the registry — the compounding mismatch DW-427 names. Both the register and the lookup side now go
    /// through <see cref="NormalizeKey"/> (trim → <c>\</c>→<c>/</c> → drop a leading <c>./</c> → invariant lower-case),
    /// so case/slash/whitespace drift resolves instead of silently falling back to the box.</para>
    ///
    /// <para><b>Determinism.</b> Pure authoring/presentation string logic, Godot-free, no <c>float</c>. It touches no
    /// sim array, is folded into no hash (<c>MeshPath</c> is explicitly EXCLUDED from <see cref="ContentHash"/> as
    /// presentation-only), and moves no golden.</para>
    /// </summary>
    public static class MeshPathId
    {
        /// <summary>The Godot project-resource scheme prefix. A <c>mesh_path</c> starting with this is form 1 (a
        /// project resource) and is never looked up in the asset registry — mirroring
        /// <c>MeshLoader.LoadFromGlb</c>'s own <c>StartsWith("res://", Ordinal)</c> routing test.</summary>
        public const string ResPrefix = "res://";

        /// <summary>How many registered ids <see cref="DescribeRegistryMiss"/> lists before summarizing the rest as
        /// "(+N more)" — a bounded log line even for a package with hundreds of assets.</summary>
        public const int MaxListedIds = 12;

        /// <summary>Form 1: a <c>res://</c> project resource path (case-sensitive prefix, whitespace-trimmed).
        /// Null/blank is neither form.</summary>
        public static bool IsProjectResourcePath(string? meshPath)
            => !string.IsNullOrWhiteSpace(meshPath)
               && meshPath!.Trim().StartsWith(ResPrefix, StringComparison.Ordinal);

        /// <summary>Form 2: a package asset id — any non-blank <c>mesh_path</c> that is NOT a <c>res://</c> path. This
        /// is exactly the routing test the render path uses to decide "consult the ingested asset registry", so the
        /// lint and the renderer can never disagree about which paths are filesystem-checkable.</summary>
        public static bool IsPackageAssetId(string? meshPath)
            => !string.IsNullOrWhiteSpace(meshPath) && !IsProjectResourcePath(meshPath);

        /// <summary>
        /// The canonical registry key for a package asset id: trimmed, back-slashes folded to <c>/</c>, a leading
        /// <c>./</c> dropped, invariant lower-case. Null/blank → <c>""</c>. Applied on BOTH sides of the registry
        /// (register + look up) so the two can never drift; invariant (not current-culture) lower-casing keeps the
        /// mapping identical on every machine/locale.
        /// </summary>
        public static string NormalizeKey(string? logicalId)
        {
            if (string.IsNullOrWhiteSpace(logicalId)) return "";

            string s = logicalId!.Trim().Replace('\\', '/');
            while (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
            return s.ToLowerInvariant();
        }

        /// <summary>The last <c>/</c>-separated segment of a path/id (its filename), normalized by
        /// <see cref="NormalizeKey"/>. <c>""</c> when there is nothing to name. Used for the "did you mean" hint on a
        /// miss — a bare-filename <c>mesh_path</c> is the most common near-miss form.</summary>
        public static string FileNameOf(string? meshPath)
        {
            string key = NormalizeKey(meshPath);
            if (key.Length == 0) return "";
            int slash = key.LastIndexOf('/');
            return slash >= 0 ? key[(slash + 1)..] : key;
        }

        /// <summary>
        /// DW-427's diagnostic: the human-readable reason a <c>mesh_path</c> did not resolve to an ingested custom
        /// asset, naming the authored value, the normalized key that was looked up, the ids that ARE registered
        /// (ordinal-sorted, capped at <see cref="MaxListedIds"/>), a filename-level "did you mean" when one matches,
        /// and the authoring convention itself. Returned WITHOUT a log tag so the caller owns its own prefix (e.g.
        /// <c>"[MeshLoader] "</c>) and so this is Tier-1 assertable. Never throws: a null/empty id list is reported as
        /// "no custom assets are registered", which is itself the answer when a package was never ingested.
        /// </summary>
        public static string DescribeRegistryMiss(string? meshPath, IEnumerable<string>? registeredIds)
        {
            string authored = meshPath ?? "";
            string key = NormalizeKey(meshPath);

            // Materialize once (the caller may hand us a lazy view over a live dictionary) and order deterministically
            // so the same miss always logs the same line.
            List<string> ids = (registeredIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder();
            sb.Append($"mesh_path '{authored}' did not resolve to any ingested custom asset ")
              .Append($"(looked up normalized key '{key}') — rendering the box placeholder. ");

            if (ids.Count == 0)
            {
                sb.Append("No custom assets are registered at all: the unit's package was never ingested, or its ")
                  .Append("manifest asset_files list is empty. ");
            }
            else
            {
                sb.Append($"Registered asset ids ({ids.Count}): ")
                  .Append(string.Join(", ", ids.Take(MaxListedIds)));
                if (ids.Count > MaxListedIds) sb.Append($" (+{ids.Count - MaxListedIds} more)");
                sb.Append(". ");

                string fileName = FileNameOf(meshPath);
                if (fileName.Length > 0)
                {
                    string? nearMiss = ids.FirstOrDefault(id => FileNameOf(id) == fileName);
                    if (nearMiss != null) sb.Append($"Did you mean '{nearMiss}'? ");
                }
            }

            sb.Append("Convention: mesh_path is either a 'res://' project path or a package's zip-relative logical ")
              .Append("id exactly as listed in its manifest asset_files (e.g. 'assets/heavy_tank.glb').");

            return sb.ToString();
        }
    }
}
