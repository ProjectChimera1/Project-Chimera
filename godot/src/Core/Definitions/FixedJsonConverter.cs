#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The single quantization boundary (AR-14) for the 16.16 <see cref="Fixed"/> type.
    ///
    /// Converts a JSON number to <see cref="Fixed"/> at deserialize time, fronting the otherwise
    /// unguarded <see cref="Fixed.FromFloat"/> and rejecting NaN / ±Infinity / over-range
    /// (|value| &gt;= 32768, which would overflow Fixed.FromFloat's <c>(int)(value * 65536)</c> cast
    /// and silently wrap — the exact ≥32768 hazard Story 1.3b's review deferred here) with a located
    /// <see cref="JsonException"/> (System.Text.Json decorates the exception with the JSON property path).
    ///
    /// This is the ONLY place <see cref="Fixed.FromFloat"/> may run on external data; the 30 Hz
    /// simulation tick does Fixed-only math. Registered on every <see cref="JsonSerializerOptions"/>
    /// instance that deserializes content carrying <see cref="Fixed"/> fields (the scenario path and
    /// the two AI-ingest paths). The <see cref="Write"/> path emits a human-readable decimal; it runs
    /// only on save (never in-tick), and the canonical hash uses <see cref="Fixed.Raw"/>, not this text.
    /// </summary>
    public sealed class FixedJsonConverter : JsonConverter<Fixed>
    {
        /// <summary>
        /// The 16.16 representable range is roughly [-32768, 32767.99998]; any |value| &gt;= this overflows
        /// the <c>(int)(value * 65536)</c> cast in <see cref="Fixed.FromFloat"/> and wraps. Reject at the boundary.
        /// </summary>
        private const double FixedRangeLimit = 32768d;

        public override Fixed Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number)
                throw new JsonException($"Expected a JSON number for Fixed, got {reader.TokenType}.");

            double d = reader.GetDouble();
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new JsonException($"Fixed value must be finite; got {d}.");

            // Range-check the POST-CAST float, not the double. Fixed.FromFloat does (int)(value * 65536) on the
            // float, so a double just under +32768 that rounds UP to 32768f when narrowed would overflow that cast
            // to int.MinValue — a silent sign flip (the exact wrap AC1 must reject). 16.16 represents
            // [-32768, 32767.99998]; -32768f is exactly representable (raw int.MinValue) and must NOT be rejected.
            float f = (float)d;
            if (f >= FixedRangeLimit || f < -FixedRangeLimit)
                throw new JsonException(
                    $"Fixed value {d} is out of 16.16 range ([-{FixedRangeLimit}, {FixedRangeLimit})).");

            return Fixed.FromFloat(f); // the sole allow-listed FromFloat call on external data
        }

        public override void Write(Utf8JsonWriter writer, Fixed value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value.ToFloat());
    }
}
