#nullable enable

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The tuned default combat-feedback look set (Story 2.7, AC1). This is the CANONICAL embedded
    /// source of truth for the four event-type defaults — byte-for-byte the as-built
    /// <c>CombatFeedbackBridge</c> constants (UX-DR51 "per as-built bridge").
    ///
    /// It is embedded (not a <c>res://</c> JSON) precisely so the Godot-free Tier-1
    /// "default-equals-constants" test can read it from the test assembly. The bridge reads it for the
    /// null-profile fallback (each <c>CombatEvent</c> with no override renders the matching default),
    /// translating these primitives to Godot materials/values at the presentation boundary.
    ///
    /// Presentation-domain — excluded from <c>SimChecksum</c>/canonical hash (the sim never reads it).
    /// Feedback is cosmetic, not balance, and is fully overridable per unit/ability, so an embedded
    /// default still honours the data-driven rule (creators reach it via override).
    /// </summary>
    public static class CombatFeedbackDefaults
    {
        /// <summary>MeleeHit — orange, the as-built <c>_matMelee</c> + <c>SpawnFlash(.., 0.9, 0.18, ..)</c>.</summary>
        public static readonly FlashSpec Melee = new()
        {
            ColorRgb = new[] { 1.0f, 0.50f, 0.10f },
            EmissionMult = 3.0f,
            Scale = 0.9f,
            DurationSec = 0.18f,
        };

        /// <summary>RangedHit — yellow, the as-built <c>_matRanged</c> + <c>SpawnFlash(.., 0.7, 0.15, ..)</c>.</summary>
        public static readonly FlashSpec Ranged = new()
        {
            ColorRgb = new[] { 1.0f, 0.85f, 0.10f },
            EmissionMult = 2.5f,
            Scale = 0.7f,
            DurationSec = 0.15f,
        };

        /// <summary>SplashHit — red, the as-built <c>_matSplash</c> + <c>SpawnFlash(.., 1.8, 0.28, ..)</c>.</summary>
        public static readonly FlashSpec Splash = new()
        {
            ColorRgb = new[] { 1.0f, 0.20f, 0.05f },
            EmissionMult = 4.0f,
            Scale = 1.8f,
            DurationSec = 0.28f,
        };

        /// <summary>UnitKilled — white, the as-built <c>_matDeath</c> + <c>SpawnFlash(.., 1.2, 0.25, ..)</c>.</summary>
        public static readonly FlashSpec Kill = new()
        {
            ColorRgb = new[] { 1.0f, 0.95f, 0.80f },
            EmissionMult = 5.0f,
            Scale = 1.2f,
            DurationSec = 0.25f,
        };

        /// <summary>The default kill camera shake — the as-built <c>SetShake(0.12f, 0.22f)</c> (duration, strength).</summary>
        public static readonly ShakeSpec KillShake = new()
        {
            DurationSec = 0.12f,
            Strength = 0.22f,
        };
    }
}
