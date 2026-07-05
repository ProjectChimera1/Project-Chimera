#nullable enable
using System.Collections.Generic;
using Godot;

namespace ProjectChimera.UI.Theme
{
    /// <summary>
    /// The canonical UI design-token vocabulary and values for Project Chimera (Story 3.1a).
    ///
    /// This is the single C# source of truth that <see cref="ThemeBuilder"/> assembles into the
    /// committed <c>main.tres</c>, and that <see cref="AccentController"/> reads to switch accents.
    /// Every later UI story (3.1b component kit, 3.1c composites, 3.3–3.7 editors, 3.11 shell) reads
    /// tokens by these <see cref="StringName"/> constants — never by magic string or literal color.
    ///
    /// Values are UX-DR1..UX-DR12 + UX-DR34, dark theme only. oklch source colors are pre-converted to
    /// sRGB hex (Godot has no oklch); consume via <c>Color.FromHtml("#rrggbb"|"#rrggbbaa")</c>
    /// (the C# name; GDScript's is <c>Color.html()</c>).
    ///
    /// NAMING: Godot <c>Theme</c> item names must be valid identifiers — hyphens are rejected by
    /// <c>Theme::is_valid_item_name()</c> (silently dropped). The CSS tokens (<c>surface-1</c>,
    /// <c>accent-bright</c>, <c>t-md</c>) therefore map to underscore names here (<c>surface_1</c>,
    /// <c>accent_bright</c>, <c>t_md</c>) — the Godot-idiomatic convention (cf. <c>font_color</c>).
    ///
    /// Presentation layer — <c>using Godot;</c> is expected here (this is NOT simulation code).
    /// </summary>
    public static class ThemeTokens
    {
        /// <summary>The custom theme type every token is stored under (D-4). Read via GetThemeColor(name, Type).</summary>
        public const string Type = "Chimera";

        // ── Token-name vocabulary (StringName constants — the vocabulary later stories depend on) ──
        // StringName is not a compile-time constant, so these are static readonly. Names use underscores
        // (Godot rejects hyphens in Theme item names).

        // Surfaces (UX-DR1)
        public static readonly StringName SurfaceVoid = "void";
        public static readonly StringName Surface0    = "surface_0";
        public static readonly StringName Surface1    = "surface_1";
        public static readonly StringName Surface2    = "surface_2";
        public static readonly StringName Surface3    = "surface_3";
        public static readonly StringName Surface4    = "surface_4";

        // Lines (UX-DR2)
        public static readonly StringName Line       = "line";
        public static readonly StringName LineStrong = "line_strong";
        public static readonly StringName EdgeLight  = "edge_light";

        // Text (UX-DR3)
        public static readonly StringName TextHi       = "text_hi";
        public static readonly StringName TextMid      = "text_mid";
        public static readonly StringName TextLo       = "text_lo";
        public static readonly StringName TextDisabled = "text_disabled";

        // Accent set (UX-DR4) — these 6 are what AccentController rewrites on switch.
        public static readonly StringName Accent       = "accent";
        public static readonly StringName AccentBright = "accent_bright";
        public static readonly StringName AccentDim    = "accent_dim";
        public static readonly StringName AccentInk    = "accent_ink";
        public static readonly StringName AccentGlow   = "accent_glow";
        public static readonly StringName AccentWash   = "accent_wash";

        /// <summary>The 6 accent Color tokens, in the order the palettes provide their hex values.</summary>
        public static readonly StringName[] AccentTokens =
            { Accent, AccentBright, AccentDim, AccentInk, AccentGlow, AccentWash };

        // Semantic (UX-DR5)
        public static readonly StringName Ok        = "ok";
        public static readonly StringName OkInk     = "ok_ink";
        public static readonly StringName Warn      = "warn";
        public static readonly StringName WarnInk   = "warn_ink";
        public static readonly StringName Danger    = "danger";
        public static readonly StringName DangerInk = "danger_ink";
        public static readonly StringName Info      = "info";

        // Team (UX-DR6) — RESERVED: world units / minimap only, NEVER UI chrome.
        public static readonly StringName Team1 = "team_1";
        public static readonly StringName Team2 = "team_2";
        public static readonly StringName Team3 = "team_3";
        public static readonly StringName Team4 = "team_4";
        public static readonly StringName Team5 = "team_5";
        public static readonly StringName Team6 = "team_6";
        public static readonly StringName Team7 = "team_7";
        public static readonly StringName Team8 = "team_8";

        // Font roles (UX-DR7 + UX-DR34)
        public static readonly StringName FontDisplay = "font_display"; // Chakra Petch
        public static readonly StringName FontUi      = "font_ui";      // Space Grotesk (body default)
        public static readonly StringName FontMono    = "font_mono";    // JetBrains Mono
        public static readonly StringName MonoTnum    = "mono_tnum";    // JetBrains Mono + tabular figures

        // Type scale (UX-DR8) — font_size items
        public static readonly StringName T2xs = "t_2xs";
        public static readonly StringName Txs  = "t_xs";
        public static readonly StringName Tsm  = "t_sm";
        public static readonly StringName Tmd  = "t_md"; // body / default
        public static readonly StringName Tlg  = "t_lg";
        public static readonly StringName Txl  = "t_xl";
        public static readonly StringName T2xl = "t_2xl";
        public static readonly StringName T3xl = "t_3xl";
        public static readonly StringName T4xl = "t_4xl";
        public static readonly StringName T5xl = "t_5xl";

        // Spacing (UX-DR10) — constant items
        public static readonly StringName S1 = "s1";
        public static readonly StringName S2 = "s2";
        public static readonly StringName S3 = "s3";
        public static readonly StringName S4 = "s4";
        public static readonly StringName S5 = "s5";
        public static readonly StringName S6 = "s6";
        public static readonly StringName S7 = "s7";
        public static readonly StringName S8 = "s8";

        // Chamfer cut sizes (UX-DR9) — constant items
        public static readonly StringName Cut   = "cut";     // panels (8)
        public static readonly StringName CutSm = "cut_sm";  // btn/input/chip/menu (5)
        public static readonly StringName CutLg = "cut_lg";  // dialogs (14)

        // Motion (UX-DR50) — constant item
        public static readonly StringName Speed = "speed";   // ms

        // Shadow recipe names (UX-DR11) — documented; realized on styleboxes by components.
        public static readonly StringName Shadow1   = "shadow_1";
        public static readonly StringName Shadow2   = "shadow_2";
        public static readonly StringName ShadowPop = "shadow_pop";

        // Default body size = t-md (15px).
        public const int DefaultFontSize = 15;

        // ── Canonical values (the Canonical Token Table, 1:1) ──

        /// <summary>Non-accent color tokens: (token, sRGB hex). Accent tokens come from <see cref="AccentPalettes"/>.</summary>
        public static readonly IReadOnlyList<(StringName Name, string Hex)> ColorTokens = new (StringName, string)[]
        {
            // Surfaces (UX-DR1)
            (SurfaceVoid, "#0a0c0f"), (Surface0, "#0f1216"), (Surface1, "#14181d"),
            (Surface2, "#1a1f26"), (Surface3, "#222831"), (Surface4, "#2c333d"),
            // Lines (UX-DR2)
            (Line, "#2a3038"), (LineStrong, "#3a424d"), (EdgeLight, "#4a5562"),
            // Text (UX-DR3)
            (TextHi, "#eef2f6"), (TextMid, "#aeb7c2"), (TextLo, "#727c88"), (TextDisabled, "#4b545f"),
            // Semantic (UX-DR5) incl. *_ink
            (Ok, "#6ed274"), (OkInk, "#06210f"), (Warn, "#f0b135"), (WarnInk, "#241803"),
            (Danger, "#f05653"), (DangerInk, "#2a0606"), (Info, "#65b4e9"),
            // Team (UX-DR6) — RESERVED (present in the vault, styled onto no chrome)
            (Team1, "#2a7fd4"), (Team2, "#e06a1b"), (Team3, "#16a37a"), (Team4, "#cf72ad"),
            (Team5, "#5cb8ec"), (Team6, "#f0c000"), (Team7, "#9a6cf0"), (Team8, "#9aa3ad"),
        };

        /// <summary>Type-scale font sizes in px (UX-DR8, ratio 1.250).</summary>
        public static readonly IReadOnlyList<(StringName Name, int Px)> FontSizeTokens = new (StringName, int)[]
        {
            (T2xs, 11), (Txs, 12), (Tsm, 13), (Tmd, 15), (Tlg, 18),
            (Txl, 23), (T2xl, 29), (T3xl, 37), (T4xl, 52), (T5xl, 72),
        };

        /// <summary>Spacing + chamfer-cut + motion integer constants (UX-DR9/10/50).</summary>
        public static readonly IReadOnlyList<(StringName Name, int Value)> ConstantTokens = new (StringName, int)[]
        {
            // Spacing (UX-DR10)
            (S1, 4), (S2, 8), (S3, 12), (S4, 16), (S5, 24), (S6, 32), (S7, 48), (S8, 64),
            // Chamfer cuts (UX-DR9)
            (Cut, 8), (CutSm, 5), (CutLg, 14),
            // Motion (UX-DR50)
            (Speed, 130),
        };

        /// <summary>A UX-DR11 drop-shadow recipe for a StyleBoxFlat (dark theme; single drop layer).</summary>
        public readonly record struct ShadowRecipe(StringName Name, int Size, int OffsetX, int OffsetY, float Alpha);

        /// <summary>Shadow recipes (UX-DR11). css blur ≈ 2× Godot shadow_size, spread 0.</summary>
        public static readonly IReadOnlyList<ShadowRecipe> ShadowRecipes = new[]
        {
            new ShadowRecipe(Shadow1,   7,  0,  4,  0.45f),
            new ShadowRecipe(Shadow2,   15, 0,  10, 0.55f),
            new ShadowRecipe(ShadowPop, 25, 0,  18, 0.65f),
        };

        /// <summary>Look up a shadow recipe by name.</summary>
        public static ShadowRecipe GetShadow(StringName name)
        {
            foreach (var s in ShadowRecipes)
                if (s.Name == name) return s;
            return ShadowRecipes[0];
        }

        // ── Accent palettes (UX-DR4) ──

        /// <summary>
        /// One accent palette: the 6 accent colors as sRGB hex, in <see cref="AccentTokens"/> order.
        /// glow/wash carry 8-digit alpha hex; ink is opaque.
        /// </summary>
        public readonly record struct AccentPalette(
            string Name, string Accent, string Bright, string Dim, string Ink, string Glow, string Wash)
        {
            /// <summary>The 6 hex values in <see cref="AccentTokens"/> order, ready for Color.FromHtml.</summary>
            public string[] HexInTokenOrder => new[] { Accent, Bright, Dim, Ink, Glow, Wash };
        }

        /// <summary>The default accent applied to the committed theme.</summary>
        public const string DefaultAccent = "teal";

        /// <summary>The three switchable accent palettes (converted oklch→sRGB from the Canonical Token Table).</summary>
        public static readonly IReadOnlyList<AccentPalette> AccentPalettes = new[]
        {
            new AccentPalette("teal",   "#1ed1cd", "#4cece7", "#1f9996", "#04201e", "#1ed1cd47", "#1ed1cd1f"),
            new AccentPalette("amber",  "#f2af48", "#ffcb63", "#b77f39", "#271700", "#f2af484c", "#f2af481f"),
            new AccentPalette("violet", "#b296ff", "#cfb2ff", "#8168be", "#170a2b", "#b296ff4c", "#b296ff21"),
        };

        /// <summary>Find a palette by name (case-insensitive); returns false if unknown.</summary>
        public static bool TryGetPalette(string name, out AccentPalette palette)
        {
            foreach (var p in AccentPalettes)
            {
                if (p.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                {
                    palette = p;
                    return true;
                }
            }
            palette = AccentPalettes[0];
            return false;
        }
    }
}
