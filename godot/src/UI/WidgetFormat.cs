#nullable enable
using System.Globalization;
using ProjectChimera.Core;   // Fixed
using ProjectChimera.Dsl;    // DslValueType

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 7.8 — the PRESENTATION-SIDE value formatters for the custom-UI read rail. Every int→string /
    /// Fixed→mm:ss conversion happens HERE (never in the deterministic tick — strings never enter the sim). The
    /// read rail publishes only typed raw values (<see cref="DslValueType"/>, <c>raw0</c>, <c>raw1</c>, version);
    /// the renderer calls these to turn the latest raw into display text only when the bound variable's version
    /// changes. It parallels the ad-hoc mm:ss shape used inline in <c>MainScene</c> / <c>OnboardingPanel</c> — those
    /// pre-existing sites are unchanged and are NOT routed through here (this helper clamps negatives to <c>0:00</c>;
    /// they do not).
    /// </summary>
    public static class WidgetFormat
    {
        private const int DefaultTicksPerSecond = 30; // matches SimulationLoop's fixed-timestep rate

        /// <summary>An integer as a culture-invariant string.</summary>
        public static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// A scalar variable's raw value as display text: Int/Bool/refs → the integer; Fixed → a trimmed decimal
        /// (culture-invariant, computed from the 16.16 raw — no float round-trip).
        /// </summary>
        public static string Number(DslValueType type, int raw0)
        {
            if (type != DslValueType.Fixed) return Int(raw0);
            // Presentation-side (src/UI) — float is permitted here; format invariantly, trimming trailing zeros.
            return Fixed.FromRaw(raw0).ToFloat().ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A timer variable's raw value as <c>m:ss</c>. A Fixed bind is interpreted as SECONDS (floored to whole
        /// seconds); an Int bind is interpreted as TICKS and converted via <paramref name="ticksPerSecond"/>.
        /// Negative/expired values clamp to 0:00.
        /// </summary>
        public static string MmSs(DslValueType type, int raw0, int ticksPerSecond = DefaultTicksPerSecond)
        {
            int totalSeconds = type == DslValueType.Fixed
                ? Fixed.FromRaw(raw0).ToInt()
                : (ticksPerSecond > 0 ? raw0 / ticksPerSecond : raw0);
            if (totalSeconds < 0) totalSeconds = 0;
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return minutes.ToString(CultureInfo.InvariantCulture) + ":" + seconds.ToString("D2", CultureInfo.InvariantCulture);
        }

        /// <summary>A progress fraction in [0,1] from a scalar raw over an integer denominator. A Fixed bind keeps
        /// its sub-integer precision (presentation-side float is permitted here — a fractional Fixed fills the bar
        /// proportionally rather than stepping by whole units); an Int bind divides directly. Clamped to [0,1].
        /// Presentation-only — the sim never computes this.</summary>
        public static double Fraction(DslValueType type, int raw0, int max)
        {
            if (max <= 0) return 0.0;
            double value = type == DslValueType.Fixed ? Fixed.FromRaw(raw0).ToFloat() : raw0;
            double f = value / max;
            return f < 0.0 ? 0.0 : (f > 1.0 ? 1.0 : f);
        }
    }
}
