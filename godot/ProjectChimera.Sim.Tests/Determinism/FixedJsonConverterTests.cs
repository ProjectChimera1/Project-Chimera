#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Determinism
{
    /// <summary>
    /// Story 1.4 (AC1) — proves <see cref="FixedJsonConverter"/> is the single quantization boundary (AR-14):
    /// it quantizes a JSON number to <see cref="Fixed"/> identically to <see cref="Fixed.FromFloat"/> (same raw,
    /// so no hashed value moves when a field migrates float → Fixed), and rejects NaN / ±Infinity / over-range
    /// (|value| ≥ 32768) with a LOCATED <see cref="JsonException"/> rather than silently wrapping. The over-range
    /// case pins the ≥32768 overflow that Story 1.3b's review deferred here.
    ///
    /// Every expected raw is derived INDEPENDENTLY (from Fixed.FromFloat/FromInt or a hand-computed constant),
    /// never by re-running the converter under test (the 1.1 "a tautological assert proves nothing" rule).
    /// </summary>
    public class FixedJsonConverterTests
    {
        /// <summary>A minimal content record with one Fixed field — mirrors how TriggerAction.Amount now binds.</summary>
        private sealed class FixedHolder
        {
            [JsonPropertyName("amount")] public Fixed Amount { get; set; }
        }

        private static readonly JsonSerializerOptions Options = new()
        {
            Converters = { new FixedJsonConverter() },
        };

        private static FixedHolder Deserialize(string json) =>
            JsonSerializer.Deserialize<FixedHolder>(json, Options)!;

        // ── Quantization: same raw as Fixed.FromFloat (the byte-identical-golden guarantee) ──────────

        [Fact]
        public void FractionalNumber_QuantizesIdenticalRawToFromFloat()
        {
            FixedHolder h = Deserialize(@"{""amount"": 12.5}");
            Assert.Equal(Fixed.FromFloat(12.5f).Raw, h.Amount.Raw);
            // Independent pin: 12.5 * 65536 = 819200.
            Assert.Equal(819_200, h.Amount.Raw);
        }

        [Fact]
        public void IntegerNumber_DeserializesCorrectly()
        {
            FixedHolder h = Deserialize(@"{""amount"": 100}");
            Assert.Equal(Fixed.FromInt(100).Raw, h.Amount.Raw);
            // Independent pin: 100 * 65536 = 6,553,600 — identical to the old Fixed.FromFloat(100f) raw,
            // so a field migrating float→Fixed at an integer literal keeps its exact bits.
            Assert.Equal(6_553_600, h.Amount.Raw);
        }

        [Fact]
        public void NegativeFractional_QuantizesIdenticalRawToFromFloat()
        {
            FixedHolder h = Deserialize(@"{""amount"": -3.25}");
            Assert.Equal(Fixed.FromFloat(-3.25f).Raw, h.Amount.Raw);
        }

        // ── Rejection: NaN / ±Infinity (whatever layer throws, it surfaces as JsonException) ─────────

        [Theory]
        [InlineData(@"{""amount"": NaN}")]
        [InlineData(@"{""amount"": Infinity}")]
        [InlineData(@"{""amount"": -Infinity}")]
        public void NonFinite_IsRejected(string json)
        {
            // The codebase does not set AllowNamedFloatingPointLiterals, so a bare NaN/Infinity token is itself a
            // parse error before the converter runs; the converter's IsNaN/IsInfinity guard covers any value that
            // reaches it non-finite. Either way the surfaced type is JsonException — assert rejection regardless.
            Assert.Throws<JsonException>(() => Deserialize(json));
        }

        // ── Rejection: over-range (the D7 ≥32768 overflow), with a located error ──────────────────────

        [Theory]
        [InlineData(@"{""amount"": 32768}")]   // exactly the 16.16 overflow boundary (rejected — boundary is inclusive)
        [InlineData(@"{""amount"": 100000}")]
        [InlineData(@"{""amount"": 1e9}")]
        [InlineData(@"{""amount"": -32768}")]
        public void OverRange_IsRejectedWithLocatedError(string json)
        {
            // |value| ≥ 32768 overflows Fixed.FromFloat's (int)(value*65536) cast and wraps; the converter rejects
            // it at the boundary. System.Text.Json decorates a JsonException thrown from a converter with the JSON
            // Path, giving the AC's "located error".
            JsonException ex = Assert.Throws<JsonException>(() => Deserialize(json));
            Assert.True(ex.Path is not null && ex.Path.Contains("amount"),
                $"Expected a located error naming the 'amount' property; got Path='{ex.Path}', Message='{ex.Message}'.");
        }

        [Fact]
        public void JustInsideRange_Succeeds()
        {
            // 32767.5 is representable in 16.16 (raw 2,147,450,880 < int.MaxValue) → must NOT be rejected.
            FixedHolder h = Deserialize(@"{""amount"": 32767.5}");
            Assert.Equal(Fixed.FromFloat(32767.5f).Raw, h.Amount.Raw);
        }

        // ── Integration: the real TriggerAction fields bind through the converter ─────────────────────

        [Fact]
        public void TriggerAction_AmountAndTimerSeconds_BindThroughTheConverter()
        {
            // Mirrors ScenarioSerializer / the AI-ingest options (PropertyNameCaseInsensitive + the converter):
            // both Fixed fields on the real model must quantize at the boundary.
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new FixedJsonConverter() },
            };
            var a = JsonSerializer.Deserialize<TriggerAction>(
                @"{""type"":""add_resources"",""amount"":12.5,""timer_seconds"":1.5}", opts)!;
            Assert.Equal(Fixed.FromFloat(12.5f).Raw, a.Amount.Raw);
            Assert.Equal(Fixed.FromFloat(1.5f).Raw, a.TimerSeconds.Raw);
        }
    }
}
