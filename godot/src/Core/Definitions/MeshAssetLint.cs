#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// DW-104 — the CONTENT LINT that a shipped/authored <c>mesh_path</c> actually resolves to a file, kept
    /// deliberately OUTSIDE <see cref="FactionValidator"/>.
    ///
    /// <para><b>Why a separate lint (recorded decision, 2026-07-19, re-affirmed 2026-07-25).</b>
    /// <see cref="FactionValidator.ValidateComplete"/> only ever checked that <c>mesh_path</c> is non-blank, so a
    /// dangling path (the two <c>aviary</c> buildings, DW-102) passed the "is this faction complete/playable" gate
    /// silently. The recorded decision was NOT to teach the sim validator to read the filesystem — it is pure,
    /// Godot-free, never-throws, never-logs, and runs on every match load — but to add the disk-existence check as an
    /// explicitly-named lint at the authoring SAVE EDGE. That is this class: it performs no I/O of its own on the
    /// definition path (existence is an INJECTED <c>Func&lt;string,bool&gt;</c> probe, the
    /// <see cref="ItemDefinitionValidator.ValidateFields"/> icon-probe precedent), so it is Tier-1 testable with a
    /// fake probe and works equally with Godot's <c>ResourceLoader.Exists</c> or the plain-filesystem probe below.</para>
    ///
    /// <para><b>What it checks — and what it deliberately does not.</b> Only form-1 <c>res://</c> paths (see
    /// <see cref="MeshPathId"/>) are filesystem-checkable, so a package asset id (form 2, resolved at render time
    /// through the ingested asset registry) is SKIPPED — linting it as a missing file would block every faction that
    /// ships custom packaged art. A blank/absent <c>mesh_path</c> is also NOT reported here: that axis belongs to
    /// <see cref="FactionValidator.ValidateComplete"/> (blank = the intentional box-placeholder mid-edit state), and
    /// double-reporting it would badge one field twice.</para>
    ///
    /// <para><b>Determinism.</b> Authoring-time only. Reads authoring strings, reports strings; touches no sim array,
    /// folds into no hash (<c>MeshPath</c> is excluded from <see cref="ContentHash"/> as presentation-only), moves no
    /// golden. Godot-free.</para>
    /// </summary>
    public static class MeshAssetLint
    {
        /// <summary>The file that marks a Godot project root — the anchor <see cref="TryResolveProjectRoot"/> walks up
        /// to in order to turn a <c>res://</c> path into an absolute OS path without Godot.</summary>
        public const string ProjectMarkerFile = "project.godot";

        /// <summary>
        /// Every authored <c>res://</c> <c>mesh_path</c> in <paramref name="def"/> (units then buildings, authoring
        /// order) that <paramref name="exists"/> reports as absent, as located <c>("mesh_path", …)</c> errors in the
        /// <c>"{kind} '{id}'.{path}: {reason}"</c> shape <see cref="UnitDefinitionValidator"/> uses — which is also the
        /// shape <see cref="FactionDefinerWizardCore.StepForError"/> routes on, so a unit's error jumps the wizard to
        /// the Roster step and a building's to Buildings &amp; Tech.
        ///
        /// <para>Returns an EMPTY list (never null, never throws) when <paramref name="def"/> is null or
        /// <paramref name="exists"/> is null — a caller with no probe available (an exported build, a Tier-1 test)
        /// gets today's behavior unchanged rather than a false "everything is missing" reject.</para>
        /// </summary>
        public static IReadOnlyList<(string FieldPath, string Message)> FindMissingMeshFiles(
            FactionDefinition? def, Func<string, bool>? exists)
        {
            var errors = new List<(string, string)>();
            if (def is null || exists is null) return errors;

            foreach (UnitDefinition u in def.Units ?? new List<UnitDefinition>())
            {
                if (u is null) continue;
                AddIfMissing(errors, exists, "unit", u.Id ?? "", u.MeshPath);
            }
            foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
            {
                if (b is null) continue;
                AddIfMissing(errors, exists, "building", b.Id ?? "", b.MeshPath);
            }

            return errors;
        }

        /// <summary>
        /// A Godot-free existence probe for <c>res://</c> paths rooted at <paramref name="projectRootAbsolute"/> (the
        /// directory holding <see cref="ProjectMarkerFile"/>): <c>res://a/b.glb</c> → <c>&lt;root&gt;/a/b.glb</c> →
        /// <see cref="File.Exists"/>. Returns false — i.e. "missing", fail-closed — for anything it cannot map: a
        /// non-<c>res://</c> path, an empty remainder, or a path containing <c>..</c> (which could otherwise probe
        /// outside the project). Never throws (an I/O/permission error reads as missing).
        /// </summary>
        public static Func<string, bool> MakeResExistsProbe(string projectRootAbsolute)
        {
            string root = projectRootAbsolute ?? "";
            return path =>
            {
                if (!MeshPathId.IsProjectResourcePath(path)) return false;

                string rel = path.Trim()[MeshPathId.ResPrefix.Length..].Trim();
                if (rel.Length == 0) return false;
                if (rel.Contains("..", StringComparison.Ordinal)) return false;   // never probe outside the project

                rel = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)
                         .TrimStart(Path.DirectorySeparatorChar);
                if (rel.Length == 0) return false;

                try { return File.Exists(Path.Combine(root, rel)); }
                catch { return false; }
            };
        }

        /// <summary>
        /// <see cref="MakeResExistsProbe"/> for a project discovered by walking UP from
        /// <paramref name="anyPathInsideProject"/> (e.g. the absolute factions directory the wizard saves into), or
        /// <c>null</c> when no <see cref="ProjectMarkerFile"/> ancestor exists. Null is the deliberate fail-OPEN
        /// answer for "there is no project tree on disk to check against" — an exported build (the assets live in the
        /// .pck, so a caller there should pass Godot's own <c>ResourceLoader.Exists</c> instead) or a unit test writing
        /// into a bare temp directory. Never throws.
        /// </summary>
        public static Func<string, bool>? TryMakeResExistsProbe(string? anyPathInsideProject)
        {
            string? root = TryResolveProjectRoot(anyPathInsideProject);
            return root is null ? null : MakeResExistsProbe(root);
        }

        /// <summary>
        /// The nearest ancestor directory of <paramref name="startPathAbsolute"/> (inclusive, and tolerant of being
        /// handed a file path) that contains <see cref="ProjectMarkerFile"/>, or <c>null</c> if none does. Mirrors the
        /// walk-up idiom the Tier-1 tests already use to find <c>resources/data/factions</c>. Never throws.
        /// </summary>
        public static string? TryResolveProjectRoot(string? startPathAbsolute)
        {
            if (string.IsNullOrWhiteSpace(startPathAbsolute)) return null;

            try
            {
                string start = startPathAbsolute!.Trim();
                DirectoryInfo? dir = Directory.Exists(start)
                    ? new DirectoryInfo(start)
                    : new FileInfo(start).Directory;

                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, ProjectMarkerFile))) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { /* an unreadable/malformed path simply has no resolvable project root */ }

            return null;
        }

        // ── Internals ────────────────────────────────────────────────────────────

        /// <summary>Append one located error when <paramref name="meshPath"/> is an authored <c>res://</c> path the
        /// probe cannot find. Blank paths (ValidateComplete's axis) and package asset ids (registry-resolved) are
        /// skipped — see the class doc.</summary>
        private static void AddIfMissing(List<(string, string)> errors, Func<string, bool> exists,
                                        string kind, string id, string? meshPath)
        {
            if (!MeshPathId.IsProjectResourcePath(meshPath)) return;   // blank or a package asset id → not our axis

            bool found;
            try { found = exists(meshPath!); }
            catch { found = false; }   // a throwing probe reads as missing — the lint never throws

            if (!found)
                errors.Add(("mesh_path", $"{kind} '{id}'.mesh_path: '{meshPath}' does not exist on disk — " +
                                         "generate/import the asset or repoint mesh_path before saving."));
        }
    }
}
