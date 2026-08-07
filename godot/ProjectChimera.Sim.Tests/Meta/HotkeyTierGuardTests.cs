#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;
using static ProjectChimera.Sim.Tests.Meta.CSharpSourceScan;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// DW-895/DW-897 — the CHORD-TIER AUDIT, made permanent.
    ///
    /// <para><b>The defect.</b> The 2026-08-04 keymap re-tier gave bare letters to modal TOOLS and
    /// <c>Ctrl+&lt;letter&gt;</c> to the twelve editors, and it is enforced at exactly one place:
    /// <c>EditorHotkeyTableTests</c>, which fails on a duplicate chord WITHIN <see cref="ProjectChimera.Core.Definitions.EditorHotkeys"/>.
    /// That guard is structurally blind to the collision that actually shipped. Godot's
    /// <c>InputEventKey.Keycode</c> is unaffected by modifiers, so a tool that switches on bare <c>Key.B</c> also
    /// matches <c>Ctrl+B</c>. Because the tool ran in <c>_Input</c> (before the GUI phase and before every
    /// <c>_UnhandledInput</c>) and did not consume the event, ONE Ctrl+B press both cycled the placement palette and
    /// opened the Building editor. Reported from live use for Ctrl+B and Ctrl+U; Ctrl+G was a third, unreported
    /// instance. Nothing in the suite could see it: both consumers "worked", and no table had a duplicate.</para>
    ///
    /// <para><b>Why a source scan.</b> The fix is a one-line bail in each tool, and the thing that rots is the
    /// INVARIANT, not the fix — a tool added later, or an existing tool given a new bare-letter binding, reopens the
    /// hole with no behavioural test able to notice (there is no prior behaviour to regress). Only a scan of the
    /// shipping tree can see it. Same mechanism and portable <see cref="CallerFilePathAttribute"/> tree location as
    /// <c>PositionWriterGuardTests</c> / <c>NullableContextHygieneTests</c>.</para>
    ///
    /// <para><b>What turns it red.</b> A file under <c>godot/src/**</c> that claims a bare letter key inside an input
    /// handler without carrying the Ctrl bail — i.e. a new editor-tier collision waiting to happen.</para>
    /// </summary>
    public class HotkeyTierGuardTests
    {
        /// <summary>
        /// Files that claim a bare-letter key in an input handler and therefore MUST carry a
        /// <c>CtrlPressed</c> bail so the editor tier keeps its chords.
        ///
        /// <para>This list is deliberately explicit rather than "every file that matches", because the scan cannot
        /// tell a modal tool's hotkey from an unrelated <c>Key.X</c> comparison. Adding a bare-letter binding to a new
        /// file means adding it here — the intended friction. The question to answer first: should this really be a
        /// bare letter (a modal TOOL, pressed constantly) or a <c>Ctrl+</c> chord (an EDITOR, opened occasionally)?</para>
        /// </summary>
        private static readonly string[] BareLetterToolFiles =
        {
            "UI/EntityPlacer.cs",             // Tab/B/U/G — the palette; the DW-895 offender
            "CreationSuite/TerrainBrush.cs",
            "CreationSuite/RegionTool.cs",
            "CreationSuite/PathabilityTool.cs",
            "CreationSuite/WaterTool.cs",
        };

        /// <summary>
        /// The bail idiom, in the two spellings the tools use: an explicit <c>CtrlPressed</c> early-return over the
        /// A–Z range, or a per-binding <c>&amp;&amp; !key.CtrlPressed</c> exclusion. Either proves the author
        /// considered the modifier; neither can be written by accident.
        /// </summary>
        private static readonly Regex CtrlBail = new(
            @"CtrlPressed", RegexOptions.Compiled);

        [Fact]
        public void EveryBareLetterToolCarriesTheCtrlBail()
        {
            string root = SrcRoot();
            var missing = new List<string>();

            foreach (string rel in BareLetterToolFiles)
            {
                string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path),
                    $"DW-895 chord-tier guard: '{rel}' is listed as a bare-letter tool but does not exist. " +
                    "If the file moved or was deleted, update BareLetterToolFiles.");

                string code = StripCommentsAndLiterals(File.ReadAllText(path));
                if (!CtrlBail.IsMatch(code)) missing.Add(rel);
            }

            Assert.True(missing.Count == 0,
                "DW-895: these tools claim bare-letter keys in an input handler but never test the Ctrl modifier, so " +
                "Ctrl+<letter> reaches them as a bare letter and fires BOTH the tool and the editor that owns the " +
                "chord (Godot's Keycode ignores modifiers):\n  " + string.Join("\n  ", missing) +
                "\nAdd the established bail before the key dispatch:\n" +
                "    if (key.CtrlPressed && key.Keycode >= Key.A && key.Keycode <= Key.Z) return;   // Ctrl+<letter> = editor tier");
        }

        /// <summary>
        /// The palette's key dispatch must CONSUME what it acts on. Not consuming is the other half of the DW-895
        /// double-fire: the Ctrl bail stops <c>Ctrl+B</c> reaching the palette, but a bare <c>B</c> that the palette
        /// handles and leaves unhandled would still travel on to every <c>_UnhandledInput</c> below it.
        /// </summary>
        [Fact]
        public void ThePlacementPaletteConsumesTheKeysItHandles()
        {
            string code = StripCommentsAndLiterals(
                File.ReadAllText(Path.Combine(SrcRoot(), "UI", "EntityPlacer.cs")));

            Assert.Contains("if (handled) GetViewport().SetInputAsHandled();", code, StringComparison.Ordinal);
        }

        /// <summary>
        /// DW-898: the placer must not SWALLOW Esc. It boots armed (<c>_placementActive = true</c>), so consuming Esc
        /// while armed ate the first press in every cold Create-mode session and Settings never opened — the whole
        /// reported bug. Esc may cancel a ghost here, but the event has to continue to the single Esc owner.
        /// </summary>
        [Fact]
        public void ThePlacerDoesNotSwallowEscape()
        {
            string code = StripCommentsAndLiterals(
                File.ReadAllText(Path.Combine(SrcRoot(), "UI", "EntityPlacer.cs")));

            int esc = code.IndexOf("Key.Escape", StringComparison.Ordinal);
            Assert.True(esc >= 0, "DW-898 guard: EntityPlacer no longer references Key.Escape — update this guard.");

            // Look only at the Escape branch itself (through the end of its short block), not the whole method.
            string branch = code.Substring(esc, Math.Min(320, code.Length - esc));
            Assert.DoesNotContain("SetInputAsHandled", branch, StringComparison.Ordinal);
        }

        /// <summary>
        /// DW-896 — no panel may hard-code a bare-letter close hint again.
        ///
        /// <para>All eight <c>"Close  [N]"</c>-style captions were wrong: the 2026-08-04 re-tier moved every editor to
        /// a <c>Ctrl+</c> chord and not one literal followed. Four then advertised a key that does nothing, and four a
        /// key a different tool had taken. They now render from <see cref="ProjectChimera.Core.Definitions.EditorHotkeys.CloseLabel"/>,
        /// and this scan is what stops the next panel from re-introducing a literal.</para>
        /// </summary>
        [Fact]
        public void NoPanelHardCodesABareLetterCloseHint()
        {
            var offenders = new List<string>();
            string root = SrcRoot();
            var bareLetterClose = new Regex(@"""Close\s*\[[A-Za-z]\]""", RegexOptions.Compiled);

            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(path);
                // Deliberately NOT StripCommentsAndLiterals here — the literal IS what we are hunting.
                if (bareLetterClose.IsMatch(text)) offenders.Add(Relative(root, path));
            }

            Assert.True(offenders.Count == 0,
                "DW-896: these files hard-code a single-letter close hint instead of reading the binding table, so the " +
                "caption cannot follow a re-map (this is exactly how all eight went stale):\n  " +
                string.Join("\n  ", offenders) +
                "\nUse EditorHotkeys.CloseLabel(EditorPanelId.<Panel>) instead.");
        }

        /// <summary>
        /// DW-896 — the retired advertisements stay retired. Each string below was displayed to the user while naming
        /// a binding that did not exist: <c>N=Lobby</c> outlived Story 9.7's removal of the dev-only lobby toggle by
        /// long enough for bare N to be re-taken by the Water tool, and <c>O=Maps</c> named a key with no handler
        /// anywhere in the tree.
        /// </summary>
        [Fact]
        public void RetiredKeyAdvertisementsStayDeleted()
        {
            string[] retired = { "N=Lobby", "O=Maps", "N=Multiplayer lobby" };
            var found = new List<string>();
            string root = SrcRoot();

            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(path);
                foreach (string dead in retired)
                    if (text.Contains(dead, StringComparison.Ordinal))
                        found.Add($"{Relative(root, path)}: \"{dead}\"");
            }

            Assert.True(found.Count == 0,
                "DW-896: a retired key advertisement is back in the shipping tree:\n  " + string.Join("\n  ", found));
        }

        /// <summary>The table accessors the captions now depend on (pure, Godot-free).</summary>
        [Fact]
        public void ChordForAndCloseLabelRenderTheTablesOwnChord()
        {
            Assert.Equal("Ctrl+N", ProjectChimera.Core.Definitions.EditorHotkeys.ChordFor(
                ProjectChimera.Core.Definitions.EditorPanelId.ReplayBrowser));
            Assert.Equal("Close  [Ctrl+U]", ProjectChimera.Core.Definitions.EditorHotkeys.CloseLabel(
                ProjectChimera.Core.Definitions.EditorPanelId.UnitCard));

            // Every panel resolves — the property the captions rely on to never throw at UI-build time.
            foreach (ProjectChimera.Core.Definitions.EditorPanelId id in
                     Enum.GetValues<ProjectChimera.Core.Definitions.EditorPanelId>())
                Assert.False(string.IsNullOrWhiteSpace(
                    ProjectChimera.Core.Definitions.EditorHotkeys.ChordFor(id)));
        }

        // ── scanning ─────────────────────────────────────────────────────────────

        private static string Relative(string root, string path) =>
            path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');

        /// <summary>godot/src — two directories up from this file (…/ProjectChimera.Sim.Tests/Meta/), then into src.</summary>
        private static string SrcRoot([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source directory via [CallerFilePath].");
            string root = Path.GetFullPath(Path.Combine(dir, "..", "..", "src"));
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(
                    $"DW-895 chord-tier guard could not locate the shipping source tree. Resolved path: '{root}'. " +
                    "This path is derived from [CallerFilePath]; if the project layout moved, update this guard.");
            return root;
        }
    }
}
