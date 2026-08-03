#nullable enable
namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// DW-462 (Story 11.1 follow-up) — the Godot-free single source of truth for the in-match per-slot team-color
    /// palette and the <see cref="Faction"/>→palette-index shift. Extracted from <c>FactionVisualsPhase</c> (which is
    /// Godot-coupled and excluded from the Tier-1 glob by <c>SimSources.props</c>' <c>Phases\**</c> remove) so a
    /// mapping that shipped INVERTED twice during Story 11.1 (PATCH 7 + follow-up-2, each caught only by human
    /// review) is pinned by headless RGBA assertions (<c>TeamColorSlotMappingTests</c>). Lives directly under
    /// <c>Bootstrap/</c> beside the Godot-free phase kernel (the <c>ScenePhaseOrder</c> precedent), so it is
    /// auto-globbed into the sim test assembly.
    ///
    /// <para>Channels are raw 0–1 floats (NOT bytes) so the <c>Godot.Color</c> the presentation bridge
    /// (<c>TeamColorPaletteGodot.ToColor</c>) constructs is bit-identical to the pre-extraction in-phase literals —
    /// "index 0/1 keep today's blue/red exactly" is a visual-continuity invariant. Color is presentation-only data:
    /// nothing here is folded into SimChecksum, so the advisory CHM0001 float note applies (the DelayMath
    /// precedent), never the Fixed-point rule.</para>
    /// </summary>
    public static class TeamColorPalette
    {
        /// <summary>One palette color as raw 0–1 RGB channels. Godot-free; <c>TeamColorPaletteGodot.ToColor</c> is
        /// the presentation-side bridge (alpha is always 1).</summary>
        public readonly struct Rgb
        {
            public readonly float R;
            public readonly float G;
            public readonly float B;
            public Rgb(float r, float g, float b) { R = r; G = g; B = b; }
            public override string ToString() => $"({R}, {G}, {B})";
        }

        // Story 11.1 — the deterministic per-slot-index team palette (2→4 honest extension). Index 0/1 are the
        // original P1 blue / P2 red VERBATIM; 2/3 add distinct green/gold for 3–4 player maps. Keyed by the
        // POST-TRANSFORM contiguous LAUNCH index (SkirmishSetupToScenario renumbers active slots Human-first to
        // 0..k-1), never by a setup-screen row ordinal — see SkirmishSetupToScenario.LaunchIndexBySlot (DW-460).
        // Color is a per-slot-index presentation choice, NOT a data channel — no ScenarioPlayerSlot color field.
        private static readonly Rgb[] Colors =
        {
            new(0.2f, 0.5f, 1.0f),   // launch index 0 (Player1) = blue
            new(1.0f, 0.3f, 0.2f),   // launch index 1 (Player2) = red
            new(0.3f, 0.85f, 0.4f),  // launch index 2 (Player3) = green
            new(0.95f, 0.8f, 0.2f),  // launch index 3 (Player4) = gold
        };

        /// <summary>Palette size (4 — the Story-11.1 four-player ceiling). Out-of-range indices clamp, never throw.</summary>
        public static int SlotCount => Colors.Length;

        /// <summary>DW-460 — the swatch color for a setup row that launches NO player (Open/Closed): a neutral grey,
        /// deliberately outside the team palette so an inactive row never masquerades as a live team color.</summary>
        public static readonly Rgb InactiveSlotColor = new(0.5f, 0.5f, 0.5f);

        /// <summary>Resolve a per-slot-index team color, clamped to the palette range (behavior-identical to the
        /// pre-extraction <c>FactionVisualsPhase.SlotColorAt</c>).</summary>
        public static Rgb SlotColorAt(int i)
        {
            if (i < 0) i = 0;
            if (i >= Colors.Length) i = Colors.Length - 1;
            return Colors[i];
        }

        /// <summary>Resolve a faction slot's team color. The <see cref="Faction"/> enum is 1-based for players
        /// (Neutral=0, Player1=1, Player2=2, …) while the palette is 0-based (index 0 = Player1's blue), so the
        /// ordinal shifts by −1: Player1→0 (blue), Player2→1 (red), Player3→2 (green), Player4→3 (gold). THE
        /// twice-shipped Story-11.1 inversion lived exactly here: without the −1, Player1 rendered red and Player2
        /// green. Neutral (0) lands on the defensive clamp (→ index 0); production only calls this with player
        /// factions.</summary>
        public static Rgb ColorForFaction(Faction faction) => SlotColorAt((int)faction - 1);
    }
}
