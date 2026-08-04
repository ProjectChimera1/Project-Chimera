#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// The structural guarantee behind the 2026-08-04 keymap policy. Chimera had all 26 letters bound and no
    /// duplicate detection anywhere, so THREE creation-suite editors were unreachable in one build: the Tech Tree
    /// and Ability editors were swallowed by tool toggles that ran earlier in the input pipeline, and the Research card editor
    /// (a sub-inspector of the Tech Tree panel) went down transitively with its host. None of it produced an error, a log line, or a failing test — the features
    /// simply did nothing, guarded by source comments asserting the keys were free.
    ///
    /// <para>These tests make that class of failure LOUD. A duplicate chord is now a red test rather than a silently
    /// dead feature, and every declared panel must be wired to something openable.</para>
    /// </summary>
    public class EditorHotkeyTableTests
    {
        [Fact]
        public void NoTwoEditorsShareAChord()
        {
            IReadOnlyList<string> dupes = EditorHotkeys.DuplicateChords();
            Assert.True(dupes.Count == 0,
                "two editors share a chord — one of them will be unreachable, exactly the defect this table exists " +
                "to prevent: " + string.Join(", ", dupes));
        }

        [Fact]
        public void EveryPanelIdIsBound()
        {
            // A panel with no row here has no hotkey AND no dock button, so nothing can open it. Only STANDALONE
            // panels belong in the enum — the research card editor is deliberately absent because it is a
            // sub-inspector the Tech Tree panel shows on node selection, not a top-level editor.
            var bound = new HashSet<EditorPanelId>(EditorHotkeys.All.Select(h => h.Panel));
            List<EditorPanelId> missing = Enum.GetValues<EditorPanelId>().Where(p => !bound.Contains(p)).ToList();

            Assert.True(missing.Count == 0,
                "declared editor panel(s) with no binding — unreachable unless something else opens them: " +
                string.Join(", ", missing));
        }

        [Fact]
        public void EditorsDoNotStealCopyPasteUndoRedo()
        {
            // Ctrl+C/V/Z/Y are owned by copy/paste/undo/redo across every card editor; an editor claiming one would
            // break text editing in the panel it opens.
            var reserved = new[] { 'C', 'V', 'Z', 'Y' };
            List<string> clashes = EditorHotkeys.All
                .Where(h => h.Ctrl && reserved.Contains(h.Key))
                .Select(h => $"{h.Chord} ({h.Label})").ToList();

            Assert.True(clashes.Count == 0, "editor binding collides with clipboard/undo: " + string.Join(", ", clashes));
        }

        [Fact]
        public void EveryEditorIsOnTheCtrlTier()
        {
            // The policy: bare letters belong to modal tools. An editor on a bare letter is how the Tech Tree and
            // Ability editors died — it puts them back in contention with the tool that runs first.
            List<string> bare = EditorHotkeys.All.Where(h => !h.Ctrl).Select(h => $"{h.Key} ({h.Label})").ToList();
            Assert.True(bare.Count == 0, "editor bound to a bare letter, which tools own: " + string.Join(", ", bare));
        }

        [Theory]
        [InlineData('B', true)]
        [InlineData('K', true)]
        [InlineData('O', true)]
        public void Find_ResolvesADeclaredChord(char key, bool ctrl)
        {
            Assert.NotNull(EditorHotkeys.Find(key, ctrl));
            Assert.Null(EditorHotkeys.Find(key, !ctrl));   // the bare/Ctrl distinction is load-bearing
        }

        [Fact]
        public void Find_IsCaseInsensitive_AndMissesUnbound()
        {
            Assert.NotNull(EditorHotkeys.Find('b', true));
            Assert.Null(EditorHotkeys.Find('Q', true));
        }

        [Fact]
        public void ChordRendersForTheDockAndHintStrip()
        {
            EditorHotkey h = EditorHotkeys.Find('B', true)!.Value;
            Assert.Equal("Ctrl+B", h.Chord);
            Assert.Equal("Building", h.Label);
        }
    }
}
