#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>The creation-suite panels that can be opened by hotkey or from the editor dock.</summary>
    public enum EditorPanelId
    {
        BuildingCard, UnitCard, ItemCard, TechTree, FactionDefiner,
        DslGraph, Trigger, MapGenerator, Ability, ContentBrowser, ReplayBrowser, PersistenceManifest,
    }

    /// <summary>One editor binding: which chord opens it, and what to call it in the UI.</summary>
    public readonly struct EditorHotkey
    {
        /// <summary>Uppercase letter. Godot's <c>Key</c> enum matches ASCII for A–Z, so the presentation layer
        /// casts this directly — keeping this table free of any Godot type, and therefore Tier-1 testable.</summary>
        public readonly char Key;
        public readonly bool Ctrl;
        public readonly EditorPanelId Panel;
        public readonly string Label;

        public EditorHotkey(char key, bool ctrl, EditorPanelId panel, string label)
        { Key = key; Ctrl = ctrl; Panel = panel; Label = label; }

        /// <summary>"Ctrl+B" / "B" — used by both the hint strip and the dock tooltips, so they cannot drift.</summary>
        public string Chord => Ctrl ? $"Ctrl+{Key}" : Key.ToString();
    }

    /// <summary>
    /// THE binding table for every creation-suite editor — one source of truth for the key, the panel it opens, and
    /// the label the dock and hint strip show.
    ///
    /// <para>TIER POLICY (decided 2026-08-04). Bare letters belong to modal TOOLS — camera, placement, terrain,
    /// paint, snap, rotate — the things pressed hundreds of times per session. Editors, opened occasionally, live on
    /// <c>Ctrl+&lt;mnemonic&gt;</c>. F-keys stay with global overlays and the play gate. This mirrors what Blender,
    /// Unreal, Photoshop and WC3's WorldEdit all converged on, and it exists because the flat namespace failed
    /// concretely: with all 26 letters claimed, TWO editors were unreachable in one build — the Tech Tree and the
    /// Ability editor, both silently swallowed by tool toggles that ran earlier in the input pipeline. A key
    /// collision produced no error, no log, and no failing test; both were guarded by source comments asserting the
    /// key was "verified unused", which was false when written. The research card editor is a sub-inspector the
    /// Tech Tree panel shows on node selection, so it was transitively dead for as long as its host was — which is
    /// why only STANDALONE panels belong in <see cref="EditorPanelId"/>.</para>
    ///
    /// <para>Two structural guarantees ride on this table, and they are the actual fix:
    /// <list type="number">
    /// <item><c>EditorHotkeyTableTests</c> fails on a duplicate chord, so a collision is a RED TEST, never a
    /// silently dead feature.</item>
    /// <item>The editor dock builds a button per row, so every panel keeps a VISIBLE entry point and no editor is
    /// ever reachable by hotkey alone again — which is what let all three deaths go unnoticed.</item>
    /// </list></para>
    ///
    /// <para>Ctrl+C/V/Z/Y are deliberately absent: copy, paste, undo and redo own them everywhere in the editor.</para>
    /// </summary>
    public static class EditorHotkeys
    {
        public static readonly EditorHotkey[] All =
        {
            new('B', true, EditorPanelId.BuildingCard,        "Building"),
            new('U', true, EditorPanelId.UnitCard,            "Unit"),
            new('I', true, EditorPanelId.ItemCard,            "Item"),
            new('T', true, EditorPanelId.TechTree,            "Tech Tree"),
            new('F', true, EditorPanelId.FactionDefiner,      "Faction"),
            new('G', true, EditorPanelId.DslGraph,            "Graph"),
            new('L', true, EditorPanelId.Trigger,             "Triggers"),
            new('M', true, EditorPanelId.MapGenerator,        "Map Gen"),
            new('K', true, EditorPanelId.Ability,             "Ability"),
            new('O', true, EditorPanelId.ContentBrowser,      "Maps"),
            new('N', true, EditorPanelId.ReplayBrowser,       "Replays"),
            new('P', true, EditorPanelId.PersistenceManifest, "Saves"),
        };

        /// <summary>The binding for <paramref name="key"/>+<paramref name="ctrl"/>, or null when unbound.</summary>
        public static EditorHotkey? Find(char key, bool ctrl)
        {
            char upper = char.ToUpperInvariant(key);
            foreach (EditorHotkey h in All)
                if (h.Key == upper && h.Ctrl == ctrl) return h;
            return null;
        }

        /// <summary>Every chord that appears more than once — empty in a healthy table. The duplicate-detection the
        /// old flat namespace never had; see <c>EditorHotkeyTableTests</c>.</summary>
        public static IReadOnlyList<string> DuplicateChords()
        {
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            var dupes = new List<string>();
            foreach (EditorHotkey h in All)
                if (!seen.Add(h.Chord) && !dupes.Contains(h.Chord)) dupes.Add(h.Chord);
            return dupes;
        }
    }
}
