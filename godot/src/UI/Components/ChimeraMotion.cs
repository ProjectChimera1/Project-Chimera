#nullable enable
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// The single reduced-motion gate every Story 3.1c animation routes through (D-7, UX-DR29/44/50).
    ///
    /// The mock's motion is NOT uniformly 130ms: control transitions (switch / tooltip / dialog fade) read
    /// the <c>speed</c> token (130), but the toast slide is ~250ms and the spinner has its own loop
    /// durations (2.6 / 5.2 / 1.3s) — those live as <see cref="ComponentMetrics"/> constants. Whatever the
    /// duration, it passes through <see cref="Seconds"/> here so a single flag can flatten every animation.
    ///
    /// <see cref="ReducedMotion"/> is the forward-correct seam for the not-yet-built accessibility setting
    /// (UX-DR44): default off; a later Settings story flips it. When set, <see cref="Seconds"/> returns 0
    /// (transitions become instant) and the spinner checks the flag directly to stop rotating. No busy
    /// loops anywhere — everything uses Godot <c>Tween</c>/<c>_Process</c>.
    ///
    /// Presentation layer.
    /// </summary>
    public static class ChimeraMotion
    {
        /// <summary>
        /// When true, all 3.1c motion is suppressed: tweened transitions snap instantly and the spinner
        /// holds still. Default false. The seam a Settings / prefers-reduced-motion story (UX-DR44) wires.
        /// </summary>
        public static bool ReducedMotion = false;

        /// <summary>Convenience inverse of <see cref="ReducedMotion"/> for readable call sites.</summary>
        public static bool Animate => !ReducedMotion;

        /// <summary>
        /// Convert a duration in milliseconds to seconds for a <c>Tween</c>, honoring reduced-motion:
        /// returns 0 (instant) when <see cref="ReducedMotion"/> is set, else <c>ms/1000</c> (never negative).
        /// A <c>TweenProperty</c> with a 0 duration applies its final value immediately, so callers can tween
        /// unconditionally and still get the instant result under reduced-motion.
        /// </summary>
        public static double Seconds(double ms) => ReducedMotion ? 0.0 : System.Math.Max(0.0, ms) / 1000.0;

        /// <summary>
        /// The <c>speed</c> token (130ms, UX-DR50) in seconds, honoring reduced-motion — the duration for
        /// switch / tooltip / dialog transitions. Reads the token from the initialized factory (never a
        /// literal), so a token retune propagates for free.
        /// </summary>
        public static double SpeedSeconds() => Seconds(ChimeraComponents.Const(ThemeTokens.Speed));
    }
}
