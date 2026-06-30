#nullable enable
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Presentation-domain combat-feedback bundle (Story 2.7, FR-12a, AR-29).
    ///
    /// A creator-authored override for how a unit's or ability's combat events LOOK, SOUND, and FEEL:
    /// hit particles (flash), impact/death sound, screen shake, and a presentation-only hit-freeze.
    ///
    /// CRITICAL determinism contract — this type is EXCLUDED from <c>SimChecksum</c> and the canonical
    /// hash. The simulation never reads its values: it only carries a reference past the boundary
    /// (on <c>CombatEvent</c> / <c>ProjectileStore</c> / <c>EntityWorld.FeedbackProfile</c>) so the
    /// presentation bridge can resolve it at drain time. Its <c>float</c> fields are therefore NEVER
    /// quantized to <c>Fixed</c> (no sim code consumes them) and they never enter any fold.
    ///
    /// It is Godot-free (plain primitives only — no <c>Godot.Color</c>/<c>Vector3</c>/<c>AudioStream</c>,
    /// no <c>Core.Fixed</c>): the <c>CombatFeedbackBridge</c>/<c>AudioManager</c> translate these
    /// primitives to Godot types at the presentation boundary. <c>float[]</c> colour follows the
    /// <c>FactionDefinition.Color</c> precedent.
    ///
    /// Loads on BOTH content paths: <c>UnitDefinition.CombatFeedback</c> via the lenient faction loader,
    /// and <c>AbilityDefinition.CombatFeedback</c> via the strict <c>ContentJson.Options</c> loader
    /// (<c>UnmappedMemberHandling.Disallow</c> reaches nested POCOs reflectively, so EVERY sub-field
    /// below is a declared property; there are deliberately NO enum-typed fields, because the lenient
    /// faction path registers no <c>JsonStringEnumConverter</c> and would throw on a string-named enum).
    /// </summary>
    public sealed class CombatFeedbackProfile
    {
        /// <summary>Overrides the melee/ranged/splash/cast hit look for this source. Null ⇒ event-type default.</summary>
        [JsonPropertyName("hit_flash")]
        public FlashSpec? HitFlash { get; set; }

        /// <summary>Impact sound key/path under <c>res://resources/audio/sfx/</c>. Null ⇒ today's per-event clip.</summary>
        [JsonPropertyName("impact_sound")]
        public string? ImpactSoundId { get; set; }

        /// <summary>Screen shake applied on a kill event (mapped to <c>SetShake(DurationSec, Strength)</c>). Null ⇒ default kill shake.</summary>
        [JsonPropertyName("shake")]
        public ShakeSpec? Shake { get; set; }

        /// <summary>Presentation-only "hitstop": holds the hit-flash shrink for N render frames. 0 = off (today's look). NEVER pauses the sim tick.</summary>
        [JsonPropertyName("hit_freeze_frames")]
        public int HitFreezeFrames { get; set; } = 0;

        /// <summary>Overrides the kill look for this source. Null ⇒ default kill flash.</summary>
        [JsonPropertyName("death_flash")]
        public FlashSpec? DeathFlash { get; set; }

        /// <summary>Death sound key/path under <c>res://resources/audio/sfx/</c>. Null ⇒ today's kill clip.</summary>
        [JsonPropertyName("death_sound")]
        public string? DeathSoundId { get; set; }
    }

    /// <summary>
    /// A single pooled hit-flash look (presentation-only). Mirrors today's as-built bridge:
    /// an Unshaded emissive sphere whose colour×<see cref="EmissionMult"/> is the emission, spawned at
    /// <see cref="Scale"/> and linearly shrunk to zero over <see cref="DurationSec"/> seconds.
    /// All declared properties so the strict ability loader's <c>Disallow</c> accepts it.
    /// </summary>
    public sealed class FlashSpec
    {
        /// <summary>Flash colour as <c>[r, g, b]</c> in 0..1 (the <c>FactionDefinition.Color</c> float[] precedent).</summary>
        [JsonPropertyName("color_rgb")]
        public float[]? ColorRgb { get; set; }

        /// <summary>Emission multiplier — emission = colour × this (matches the as-built <c>MakeMat</c>).</summary>
        [JsonPropertyName("emission_mult")]
        public float EmissionMult { get; set; } = 1f;

        /// <summary>Spawn scale of the flash sphere.</summary>
        [JsonPropertyName("scale")]
        public float Scale { get; set; } = 1f;

        /// <summary>Lifetime in seconds (linear shrink-to-zero).</summary>
        [JsonPropertyName("duration_sec")]
        public float DurationSec { get; set; } = 0.2f;
    }

    /// <summary>
    /// A screen-shake spec (presentation-only). Maps to <c>RtsCameraController.SetShake(DurationSec, Strength)</c>
    /// — note the camera takes (duration, strength) in THAT order; <see cref="Strength"/> is in world units.
    /// </summary>
    public sealed class ShakeSpec
    {
        /// <summary>Shake duration in seconds.</summary>
        [JsonPropertyName("duration_sec")]
        public float DurationSec { get; set; }

        /// <summary>Shake strength in world units.</summary>
        [JsonPropertyName("strength")]
        public float Strength { get; set; }
    }
}
