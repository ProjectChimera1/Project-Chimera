#nullable enable
using System.IO;
using System.Text.Json;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 1.6 — proves the data-driven <see cref="DamageTable"/> (AR-26) is a behaviour-preserving lift of
    /// the retired hardcoded <c>DamageMatrix</c>:
    ///   • AC1: the original 4×5 cells load from JSON bit-for-bit identical to the in-code <see cref="DamageTable.Default"/>,
    ///     the enum integer values stay stable (Hero inserted before COUNT), and representative raws match
    ///     EXTERNALLY-computed constants (not <c>Default</c> — the 1.1 "no tautological asserts" rule).
    ///   • AC4: a malformed table (NaN / out-of-range / missing row / wrong dimensions / unknown enum key)
    ///     fails CLOSED with a located error, never a silent default.
    /// All JSON is inline (no file / embedded resource), exercising <see cref="DamageTable.FromJson"/> directly.
    /// </summary>
    public class DamageTableTests
    {
        // A complete, valid table. Mirrors resources/data/damage_table.json EXACTLY (the canonical content),
        // including a // comment to also prove ReadCommentHandling.Skip is honoured by the loader options.
        private const string Canonical = @"{
            // canonical damage table — original 4x5 values + neutral Hero row/col (Story 1.6 AC1)
            ""multipliers"": {
                ""Normal"": { ""Unarmored"": 1.0, ""Light"": 1.0, ""Medium"": 0.75, ""Heavy"": 0.5,  ""Fortified"": 0.35, ""Hero"": 1.0 },
                ""Pierce"": { ""Unarmored"": 1.5, ""Light"": 1.0, ""Medium"": 0.75, ""Heavy"": 0.35, ""Fortified"": 0.25, ""Hero"": 1.0 },
                ""Siege"":  { ""Unarmored"": 0.5, ""Light"": 0.5, ""Medium"": 1.0,  ""Heavy"": 1.0,  ""Fortified"": 1.5,  ""Hero"": 1.0 },
                ""Magic"":  { ""Unarmored"": 1.0, ""Light"": 1.0, ""Medium"": 1.0,  ""Heavy"": 1.0,  ""Fortified"": 0.5,  ""Hero"": 1.0 },
                ""Hero"":   { ""Unarmored"": 1.0, ""Light"": 1.0, ""Medium"": 1.0,  ""Heavy"": 1.0,  ""Fortified"": 1.0,  ""Hero"": 1.0 }
            }
        }";

        // A complete valid row (all 1.0) used as inert filler in the AC4 malformed tables so each defect is localised.
        private const string Row6 = @"{ ""Unarmored"": 1.0, ""Light"": 1.0, ""Medium"": 1.0, ""Heavy"": 1.0, ""Fortified"": 1.0, ""Hero"": 1.0 }";

        // The original 4×5 grid (Hero row/col excluded) — the cells that MUST stay bit-identical (AC1).
        private static readonly DamageType[] OrigDamage = { DamageType.Normal, DamageType.Pierce, DamageType.Siege, DamageType.Magic };
        private static readonly ArmorType[]  OrigArmor  = { ArmorType.Unarmored, ArmorType.Light, ArmorType.Medium, ArmorType.Heavy, ArmorType.Fortified };

        // ── AC1: original cells bit-identical to Default ──────────────────────────────────────────────

        [Fact]
        public void OriginalCells_LoadedFromJson_AreBitIdenticalToDefault()
        {
            // Default is built in code from float literals; Canonical is authored as JSON and parsed through
            // the FixedJsonConverter. Two independent construction paths agreeing proves the JSON authoring
            // reproduces the retired DamageMatrix values to the raw int (AC1) — and catches any JSON typo.
            DamageTable loaded = DamageTable.FromJson(Canonical);
            foreach (DamageType d in OrigDamage)
                foreach (ArmorType a in OrigArmor)
                    Assert.Equal(DamageTable.Default.Get(d, a).Raw, loaded.Get(d, a).Raw);
        }

        [Fact]
        public void RepresentativeCells_FromJson_MatchExternallyComputedRaws()
        {
            // Independently computed as (int)(value * 65536) — NOT read from Default/the table under test.
            DamageTable t = DamageTable.FromJson(Canonical);
            Assert.Equal(65536, t.Get(DamageType.Normal, ArmorType.Unarmored).Raw); // 1.00 → 65536
            Assert.Equal(49152, t.Get(DamageType.Normal, ArmorType.Medium).Raw);    // 0.75 → 49152
            Assert.Equal(32768, t.Get(DamageType.Normal, ArmorType.Heavy).Raw);     // 0.50 → 32768
            Assert.Equal(22937, t.Get(DamageType.Normal, ArmorType.Fortified).Raw); // 0.35f → trunc 22937
            Assert.Equal(98304, t.Get(DamageType.Pierce, ArmorType.Unarmored).Raw); // 1.50 → 98304
            Assert.Equal(16384, t.Get(DamageType.Pierce, ArmorType.Fortified).Raw); // 0.25 → 16384
        }

        [Fact]
        public void EnumIntegerValues_AreStable_WithHeroInsertedBeforeCount()
        {
            Assert.Equal(0, (int)DamageType.Normal);
            Assert.Equal(1, (int)DamageType.Pierce);
            Assert.Equal(2, (int)DamageType.Siege);
            Assert.Equal(3, (int)DamageType.Magic);
            Assert.Equal(4, (int)DamageType.Hero);
            Assert.Equal(5, (int)DamageType.COUNT);

            Assert.Equal(0, (int)ArmorType.Unarmored);
            Assert.Equal(1, (int)ArmorType.Light);
            Assert.Equal(2, (int)ArmorType.Medium);
            Assert.Equal(3, (int)ArmorType.Heavy);
            Assert.Equal(4, (int)ArmorType.Fortified);
            Assert.Equal(5, (int)ArmorType.Hero);
            Assert.Equal(6, (int)ArmorType.COUNT);
        }

        [Fact]
        public void Default_HeroRowAndColumn_AreNeutralOne()
        {
            // The neutral 1.0 placeholders (tuned in Epic 3) — full damage, within the soft-counter band.
            Assert.Equal(Fixed.FromInt(1).Raw, DamageTable.Default.Get(DamageType.Hero, ArmorType.Unarmored).Raw);
            Assert.Equal(Fixed.FromInt(1).Raw, DamageTable.Default.Get(DamageType.Normal, ArmorType.Hero).Raw);
            Assert.Equal(Fixed.FromInt(1).Raw, DamageTable.Default.Get(DamageType.Hero, ArmorType.Hero).Raw);
        }

        // ── AC4: fail closed with a located error (never a silent default) ────────────────────────────

        [Fact]
        public void NaNValue_IsRejected()
        {
            // The loader options do not set AllowNamedFloatingPointLiterals, so a bare NaN token is rejected
            // as a JsonException (parser or FixedJsonConverter guard) — never silently substituted.
            string json = @"{ ""multipliers"": {
                ""Normal"": { ""Unarmored"": NaN, ""Light"": 1.0, ""Medium"": 1.0, ""Heavy"": 1.0, ""Fortified"": 1.0, ""Hero"": 1.0 },
                ""Pierce"": " + Row6 + @", ""Siege"": " + Row6 + @", ""Magic"": " + Row6 + @", ""Hero"": " + Row6 + @"
            } }";
            Assert.Throws<JsonException>(() => DamageTable.FromJson(json));
        }

        [Fact]
        public void OutOfRangeValue_IsRejectedWithLocatedError()
        {
            // 40000 ≥ the 16.16 limit (32768): FixedJsonConverter throws a JsonException that System.Text.Json
            // decorates with the JSON Path → the AC's "offending JSON path named".
            string json = @"{ ""multipliers"": {
                ""Normal"": { ""Unarmored"": 40000, ""Light"": 1.0, ""Medium"": 1.0, ""Heavy"": 1.0, ""Fortified"": 1.0, ""Hero"": 1.0 },
                ""Pierce"": " + Row6 + @", ""Siege"": " + Row6 + @", ""Magic"": " + Row6 + @", ""Hero"": " + Row6 + @"
            } }";
            JsonException ex = Assert.Throws<JsonException>(() => DamageTable.FromJson(json));
            Assert.True(ex.Path is not null && ex.Path.Contains("Unarmored"),
                $"Expected a located error naming the offending cell; got Path='{ex.Path}', Message='{ex.Message}'.");
        }

        [Fact]
        public void MissingRow_IsRejectedWithLocatedError()
        {
            // Magic omitted entirely → InvalidDataException naming the missing row.
            string json = @"{ ""multipliers"": {
                ""Normal"": " + Row6 + @", ""Pierce"": " + Row6 + @", ""Siege"": " + Row6 + @", ""Hero"": " + Row6 + @"
            } }";
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => DamageTable.FromJson(json));
            Assert.Contains("Magic", ex.Message);
        }

        [Fact]
        public void WrongColumnCount_IsRejectedWithLocatedError()
        {
            // Normal row missing its Hero column (5 cells, not 6) → a missing cell IS a wrong dimension;
            // InvalidDataException names the offending row.
            string json = @"{ ""multipliers"": {
                ""Normal"": { ""Unarmored"": 1.0, ""Light"": 1.0, ""Medium"": 1.0, ""Heavy"": 1.0, ""Fortified"": 1.0 },
                ""Pierce"": " + Row6 + @", ""Siege"": " + Row6 + @", ""Magic"": " + Row6 + @", ""Hero"": " + Row6 + @"
            } }";
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => DamageTable.FromJson(json));
            Assert.Contains("Normal", ex.Message);
        }

        [Fact]
        public void UnknownDamageRow_IsRejected()
        {
            // An unknown damage type key ("Frost") cannot bind to the DamageType enum → located JsonException
            // at deserialize (free AC4 coverage from JsonStringEnumConverter), never a silent ignore.
            string json = @"{ ""multipliers"": {
                ""Normal"": " + Row6 + @", ""Pierce"": " + Row6 + @", ""Siege"": " + Row6 + @", ""Magic"": " + Row6 + @", ""Hero"": " + Row6 + @", ""Frost"": " + Row6 + @"
            } }";
            Assert.Throws<JsonException>(() => DamageTable.FromJson(json));
        }

        [Fact]
        public void MissingMultipliersObject_IsRejected()
        {
            // No 'multipliers' property at all → InvalidDataException, not a silent empty table.
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => DamageTable.FromJson(@"{ }"));
            Assert.Contains("multipliers", ex.Message);
        }
    }
}
