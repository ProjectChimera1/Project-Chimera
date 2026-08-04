#nullable enable
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ProjectChimera.Core;
using ProjectChimera.UI;
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// DW-406 / DW-408 — the minimap fog policy, verified Godot-free in two halves:
    ///
    /// <para><b>Policy facts</b> drive <see cref="MinimapFogPolicy"/> against a REAL ticked
    /// <see cref="FogOfWarSystem"/>: the fog-texture alpha honors the spectator RevealAll flag (DW-406 — a
    /// spectator's minimap agrees with its fully-revealed 3D overlay), and the dot gate hides enemy dots outside
    /// currently-VISIBLE fog while never hiding own dots, allied dots (shared team vision lights their cells), or
    /// anything on a fog-free minimap (DW-408 — no more all-enemy-positions leak).</para>
    ///
    /// <para><b>Wiring pins</b> (the <c>NoHardcodedPlayerCountTests</c> source-scan precedent) keep
    /// <c>MinimapBridge</c> actually routed through the policy for BOTH decisions and <c>MinimapPhase</c> actually
    /// mirroring <c>FogOfWarBridge.RevealAll</c> into the bridge — the Godot-side halves a pure policy test cannot
    /// execute. Every pin fails against the pre-fix code (raw <c>Grid</c> read, ungated dots, no reveal wiring).</para>
    /// </summary>
    public class MinimapFogPolicyTests
    {
        // Two cells far enough apart that a small vision circle around one never touches the other
        // (the FogPerspectiveTests rig).
        private static readonly FixedVec3 P1_POS = new FixedVec3(Fixed.FromInt(-40), Fixed.Zero, Fixed.FromInt(-40));
        private static readonly FixedVec3 P2_POS = new FixedVec3(Fixed.FromInt(40), Fixed.Zero, Fixed.FromInt(40));

        /// <summary>One P1 unit and one P2 unit at distant cells, small vision, fog ticked for the PLAYER1 viewer —
        /// so P1's cell is VISIBLE, P2's cell is not.</summary>
        private static FogOfWarSystem TickedPlayer1Fog()
        {
            var w = new EntityWorld();
            int p1 = w.Create(P1_POS, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int p2 = w.Create(P2_POS, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            w.VisionRange[p1] = Fixed.FromInt(4); // 4 world units = 2 cells — well short of the 80-unit separation
            w.VisionRange[p2] = Fixed.FromInt(4);
            var fog = new FogOfWarSystem(Faction.Player1);
            fog.Tick(w, Fixed.Zero);
            return fog;
        }

        // ── DW-406: the fog-texture alpha ─────────────────────────────────────────────────────────────────

        [Fact]
        public void FogAlpha_MapsEachCellState_WhenNotRevealed()
        {
            Assert.Equal(MinimapFogPolicy.FOG_VISIBLE,    MinimapFogPolicy.FogAlpha(FogOfWarSystem.VISIBLE,    revealAll: false));
            Assert.Equal(MinimapFogPolicy.FOG_EXPLORED,   MinimapFogPolicy.FogAlpha(FogOfWarSystem.EXPLORED,   revealAll: false));
            Assert.Equal(MinimapFogPolicy.FOG_UNEXPLORED, MinimapFogPolicy.FogAlpha(FogOfWarSystem.UNEXPLORED, revealAll: false));
            // An unknown byte must fail CLOSED (opaque), exactly like the bridge's old `_ =>` arm.
            Assert.Equal(MinimapFogPolicy.FOG_UNEXPLORED, MinimapFogPolicy.FogAlpha(7,   revealAll: false));
            Assert.Equal(MinimapFogPolicy.FOG_UNEXPLORED, MinimapFogPolicy.FogAlpha(255, revealAll: false));
        }

        [Fact]
        public void FogAlpha_RevealAll_ForcesEveryStateFullyClear()
        {
            // DW-406: the spectator flag must clear EVERY cell state — including unexplored — so the minimap
            // matches the RevealAll 3D overlay instead of staying fogged to the default viewer.
            byte[] states = { FogOfWarSystem.UNEXPLORED, FogOfWarSystem.EXPLORED, FogOfWarSystem.VISIBLE, 7, 255 };
            foreach (byte s in states)
                Assert.Equal(MinimapFogPolicy.FOG_VISIBLE, MinimapFogPolicy.FogAlpha(s, revealAll: true));
        }

        [Fact]
        public void FogAlpha_LessKnown_IsMoreOpaque()
        {
            // The overlay ordering the doc promises: unexplored is darkest, explored dim, visible clear.
            Assert.True(MinimapFogPolicy.FOG_UNEXPLORED > MinimapFogPolicy.FOG_EXPLORED);
            Assert.True(MinimapFogPolicy.FOG_EXPLORED > MinimapFogPolicy.FOG_VISIBLE);
            Assert.Equal(0, MinimapFogPolicy.FOG_VISIBLE); // "visible" must be FULLY transparent, not merely dimmer
        }

        // ── DW-408: the dot gate ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldDrawDot_EnemyUnderFog_IsHidden()
        {
            // The heart of DW-408: an enemy dot on a cell the local fog does NOT currently see must not draw.
            var fog = TickedPlayer1Fog();
            Assert.False(fog.IsVisible(P2_POS.X.ToFloat(), P2_POS.Z.ToFloat())); // rig sanity: enemy cell is dark
            Assert.False(MinimapFogPolicy.ShouldDrawDot(
                isOwn: false, revealAll: false, fog, P2_POS.X.ToFloat(), P2_POS.Z.ToFloat()));
        }

        [Fact]
        public void ShouldDrawDot_EnemyOnVisibleCell_Draws()
        {
            // An enemy that walked into the local player's sight must appear — the gate hides, it never blinds.
            var fog = TickedPlayer1Fog();
            Assert.True(fog.IsVisible(P1_POS.X.ToFloat(), P1_POS.Z.ToFloat())); // rig sanity: own cell is lit
            Assert.True(MinimapFogPolicy.ShouldDrawDot(
                isOwn: false, revealAll: false, fog, P1_POS.X.ToFloat(), P1_POS.Z.ToFloat()));
        }

        [Fact]
        public void ShouldDrawDot_OwnDot_AlwaysDraws_EvenOnUnexploredCells()
        {
            // Own dots never gate on fog (your own army is always on your minimap, wherever the viewer's grid is
            // dark — e.g. right after a reset before the next fog tick).
            var fog = TickedPlayer1Fog();
            float darkX = 100f, darkZ = -100f;                 // a corner nothing has ever seen
            Assert.False(fog.IsVisible(darkX, darkZ));
            Assert.True(MinimapFogPolicy.ShouldDrawDot(isOwn: true, revealAll: false, fog, darkX, darkZ));
        }

        [Fact]
        public void ShouldDrawDot_RevealAll_DrawsEnemyEverywhere()
        {
            // DW-406's dot half: a spectator (RevealAll) sees BOTH armies' dots — even on unexplored cells.
            var fog = TickedPlayer1Fog();
            Assert.True(MinimapFogPolicy.ShouldDrawDot(
                isOwn: false, revealAll: true, fog, P2_POS.X.ToFloat(), P2_POS.Z.ToFloat()));
            Assert.True(MinimapFogPolicy.ShouldDrawDot(isOwn: false, revealAll: true, fog, 100f, -100f));
        }

        [Fact]
        public void ShouldDrawDot_NoFogSystem_KeepsUngatedBehavior()
        {
            // A minimap initialized WITHOUT a fog system (fog: null) has no vision truth to gate on — it must keep
            // drawing everything, byte-identical to the pre-DW-408 behavior.
            Assert.True(MinimapFogPolicy.ShouldDrawDot(isOwn: false, revealAll: false, fog: null, 0f, 0f));
        }

        [Fact]
        public void ShouldDrawDot_AlliedDot_SurvivesTheGate_ViaSharedTeamVision()
        {
            // Regression guard for the gate's one indirect dependency: an ALLIED unit is not "own" (it paints in the
            // enemy colour today), but shared team vision (Story 9.14) lights its cell on the local grid — so the
            // fog-visibility gate must keep allied dots on the minimap without any alliance plumbing of its own.
            var alliances = new AllianceStore();
            alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1;

            var w = new EntityWorld();
            int ally = w.Create(P2_POS, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            w.VisionRange[ally] = Fixed.FromInt(4);
            var fog = new FogOfWarSystem(Faction.Player1, alliances) { SharedTeamVision = true };
            fog.Tick(w, Fixed.Zero);

            Assert.True(MinimapFogPolicy.ShouldDrawDot(
                isOwn: false, revealAll: false, fog, P2_POS.X.ToFloat(), P2_POS.Z.ToFloat()));
        }

        [Fact]
        public void Spectator_RevealAll_MinimapFullyOpen_TextureAndDots()
        {
            // DW-406's closure statement — "so the two views agree": over the REAL ticked grid, a revealed viewer
            // gets a fully transparent fog texture (every cell) AND both factions' dots, i.e. the minimap is exactly
            // as open as the RevealAll 3D overlay.
            var fog = TickedPlayer1Fog();
            foreach (byte cell in fog.Grid)
                Assert.Equal(MinimapFogPolicy.FOG_VISIBLE, MinimapFogPolicy.FogAlpha(cell, revealAll: true));

            Assert.True(MinimapFogPolicy.ShouldDrawDot(isOwn: true,  revealAll: true, fog, P1_POS.X.ToFloat(), P1_POS.Z.ToFloat()));
            Assert.True(MinimapFogPolicy.ShouldDrawDot(isOwn: false, revealAll: true, fog, P2_POS.X.ToFloat(), P2_POS.Z.ToFloat()));
        }

        // ── Wiring pins (source scan — the Godot-side halves a policy test cannot run) ────────────────────

        [Fact]
        public void MinimapBridge_RoutesBothFogDecisions_ThroughThePolicy()
        {
            string blob = StripCommentsAndNormalize(File.ReadAllText(BridgeFile()));

            // DW-406: the fog-texture fill must take its per-cell alpha from the policy (which honors RevealAll) —
            // not from a raw Grid switch like the pre-fix code.
            Assert.Contains("MinimapFogPolicy.FogAlpha(", blob);
            // DW-408: both dot loops (units + buildings) must consult the gate before painting.
            Assert.True(Regex.Matches(blob, Regex.Escape("MinimapFogPolicy.ShouldDrawDot(")).Count >= 2,
                "MinimapBridge.DrawDots must gate BOTH the unit loop and the building loop through " +
                "MinimapFogPolicy.ShouldDrawDot — one (or both) call sites are gone.");
            // DW-406: the reveal getter seam itself must exist for MinimapPhase to wire.
            Assert.Contains("public void SetRevealAll(", blob);
        }

        [Fact]
        public void MinimapPhase_MirrorsFogBridgeRevealAll_IntoTheBridge()
        {
            string blob = StripCommentsAndNormalize(File.ReadAllText(PhaseFile()));
            // The one flag every spectator site flips (FogOfWarBridge.RevealAll) must be what the minimap reads —
            // spacing-insensitive so a reformat cannot fake a regression, but the FogBridge source must stay.
            Assert.True(
                Regex.IsMatch(blob, @"SetRevealAll\s*\(\s*\(\s*\)\s*=>\s*_ctx\s*\.\s*FogBridge\s*\?\s*\.\s*RevealAll"),
                "MinimapPhase must wire minimap.SetRevealAll(() => _ctx.FogBridge?.RevealAll ?? false) — the " +
                "spectator reveal is no longer mirrored into the minimap (DW-406 regression).");
        }

        /// <summary>Remove block + line comments, then collapse all whitespace to single spaces — the scan sees code
        /// only, spacing-insensitively (the <c>NoHardcodedPlayerCountTests</c> helper).</summary>
        private static string StripCommentsAndNormalize(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"//[^\n]*", " ");
            return Regex.Replace(text, @"\s+", " ");
        }

        // ── path helpers (this file lives in godot/ProjectChimera.Sim.Tests/UI/) ──────────────────────────

        private static string BridgeFile([CallerFilePath] string thisFilePath = "") =>
            ResolveFromHere(thisFilePath, "..", "..", "src", "UI", "MinimapBridge.cs");

        private static string PhaseFile([CallerFilePath] string thisFilePath = "") =>
            ResolveFromHere(thisFilePath, "..", "..", "src", "Core", "Bootstrap", "Phases", "MinimapPhase.cs");

        private static string ResolveFromHere(string thisFilePath, params string[] segments)
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source dir via [CallerFilePath].");
            string[] parts = new string[segments.Length + 1];
            parts[0] = dir;
            Array.Copy(segments, 0, parts, 1, segments.Length);
            return Path.GetFullPath(Path.Combine(parts));
        }
    }
}
