#nullable enable
namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 3.5 — Godot-free helpers for in-editor model assignment. Homes the two pure decisions the
    /// Unit Card model row makes so they are Tier-1 testable without the engine (mirrors 3.3's
    /// <see cref="UnitCardText"/>): the "box-placeholder-is-null" normalization, and the AR-5 last-used-folder
    /// derivation from a chosen <c>res://</c> path.
    ///
    /// <para><b>Determinism posture.</b> Purely additive, unfolded, authoring-time string logic. It touches no
    /// sim array and moves no checksum or golden. No <c>using Godot;</c>, no <see cref="ProjectChimera.Core.Fixed"/>.</para>
    /// </summary>
    public static class ModelAssignment
    {
        /// <summary>
        /// Normalize an authored mesh path to the storage contract: null/empty/whitespace → <c>null</c> (the explicit
        /// box-placeholder state that <c>MeshLoader</c> renders as the box — never a sentinel string), otherwise the
        /// trimmed path. This is the single place the "blank clears to the box placeholder" rule lives.
        /// </summary>
        public static string? NormalizeMeshPath(string? raw)
            => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

        /// <summary>
        /// The parent folder of a <c>res://</c> path (everything before the last <c>'/'</c>), or <c>""</c> when the
        /// path is null/empty or has no slash. Used to reopen the Browse dialog at the last-used asset folder (AR-5).
        /// <c>"res://a/b/x.glb" → "res://a/b"</c>; <c>"x.glb" → ""</c>.
        /// </summary>
        public static string FolderOf(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path[..slash] : "";
        }
    }
}
