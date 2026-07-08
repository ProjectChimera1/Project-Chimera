#nullable enable
using System;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The closed set of Simple-mode leveling-curve bundles for the Unit Card Editor's Promote-to-Hero flow (Story 3.7,
    /// mirroring <see cref="UnitCompositionPresets"/>). Each bundle is a pure <c>Kind → (MaxLevel, BaseXp, XpGrowth,
    /// XpPerKill)</c> mapping: the Simple form offers them as a "Leveling" dropdown so a creator picks a progression
    /// shape WITHOUT hand-tuning the raw curve. Godot-free, deterministic, Tier-1-testable.
    ///
    /// <para><b>This is a Simple-mode convenience, NOT hardcoded balance a creator can't reach</b> (UX-DR54 / FR-6):
    /// Advanced mode exposes every raw hero field plus the JSON hatch. Applying a preset REPLACES the four curve fields
    /// with the bundle's values; <see cref="Detect"/> is the inverse (value-equality on the four curve fields) so the
    /// dropdown round-trips losslessly — a hand-tuned curve that matches no bundle (or a null hero) reads back as
    /// <see cref="Kind.Custom"/> and applying the dropdown never silently rewrites it.</para>
    /// </summary>
    public static class HeroLevelingPresets
    {
        /// <summary>The authored curve fields a preset carries — the four leveling fields (Story 3.7) plus the Story 3.13
        /// share-radius + per-level growth fields, so a preset stays a COMPLETE authored bundle. All presets carry the
        /// same 3.13 defaults (share 12, zero growth), so <see cref="Detect"/>'s whole-tuple equality still maps each
        /// preset uniquely back to its <see cref="Kind"/> and round-trips a freshly-promoted hero.</summary>
        public readonly record struct Curve(int MaxLevel, float BaseXp, float XpGrowth, float XpPerKill,
                                            float XpShareRadius, float HealthPerLevel, float DamagePerLevel, float ArmorPerLevel);

        /// <summary>The closed leveling registry (display order = dropdown order). <see cref="Custom"/> = "no preset" —
        /// the fallback for any curve no bundle matches (incl. a null hero).</summary>
        public enum Kind : byte
        {
            /// <summary>No preset — the curve was hand-tuned (or there is no hero). Selecting it makes no change.</summary>
            Custom = 0,
            /// <summary>The default balanced progression (the promote-on default).</summary>
            Standard = 1,
            /// <summary>A short, shallow curve — heroes level quickly.</summary>
            Fast = 2,
            /// <summary>A long, steep curve — heroes level slowly.</summary>
            Slow = 3,
        }

        /// <summary>The closed preset list for the Simple-mode dropdown (stable order).</summary>
        public static readonly (Kind Kind, string Label)[] All =
        {
            (Kind.Custom,   "Custom"),
            (Kind.Standard, "Standard"),
            (Kind.Fast,     "Fast"),
            (Kind.Slow,     "Slow"),
        };

        // The curve each bundle composes. All values are inside the validator's authoring bounds
        // (max_level ∈ [2,100], base_xp finite & > 0, xp_growth finite & ≥ 1, xp_per_kill finite & ≥ 0). The three
        // curve 4-tuples are pairwise distinct (they share xp_per_kill = 100 but differ in the other fields), so
        // Detect's whole-tuple equality maps each preset uniquely back to its Kind.
        // The Story 3.13 fields (xp_share_radius 12, all *_per_level 0) match the HeroDefinition defaults and are shared
        // by every preset, so they never break Detect's whole-tuple round-trip.
        private static readonly Curve _standard = new Curve(10, 100f, 1.15f, 100f, 12f, 0f, 0f, 0f);   // the promote-on default
        private static readonly Curve _fast     = new Curve(8,  60f,  1.10f, 100f, 12f, 0f, 0f, 0f);
        private static readonly Curve _slow     = new Curve(15, 150f, 1.25f, 100f, 12f, 0f, 0f, 0f);

        /// <summary>The <see cref="Curve"/> for <paramref name="kind"/> (<see cref="Kind.Custom"/> ⇒ the Standard curve —
        /// Custom is a no-op in the panel, never applied, but a defined return keeps the mapping total).</summary>
        public static Curve Bundle(Kind kind) => kind switch
        {
            Kind.Standard => _standard,
            Kind.Fast     => _fast,
            Kind.Slow     => _slow,
            Kind.Custom   => _standard,
            _             => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown leveling preset kind."),
        };

        /// <summary>
        /// The inverse of <see cref="Bundle"/>: the <see cref="Kind"/> whose curve exactly matches
        /// <paramref name="hero"/>'s four curve fields, or <see cref="Kind.Custom"/> when none match (incl. a null
        /// <paramref name="hero"/>). Guarantees a lossless dropdown round-trip — a preset-shaped curve reads back to its
        /// <see cref="Kind"/>, and any other curve reads back as <see cref="Custom"/>.
        /// </summary>
        public static Kind Detect(HeroDefinition? hero)
        {
            if (hero == null) return Kind.Custom;
            var have = new Curve(hero.MaxLevel, hero.BaseXp, hero.XpGrowth, hero.XpPerKill,
                                 hero.XpShareRadius, hero.HealthPerLevel, hero.DamagePerLevel, hero.ArmorPerLevel);
            foreach ((Kind kind, _) in All)
            {
                if (kind == Kind.Custom) continue;
                if (Bundle(kind) == have) return kind;
            }
            return Kind.Custom;
        }
    }
}
