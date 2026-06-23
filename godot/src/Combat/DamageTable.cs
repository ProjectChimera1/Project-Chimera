#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // FixedJsonConverter (AR-14 quantization boundary)

namespace ProjectChimera.Combat
{
    /// <summary>
    /// The type of damage an attacker deals. Integer values are STABLE keys (AR-26 / Story 1.6 AC1):
    /// Hero is inserted immediately before <see cref="COUNT"/> so every pre-existing value is unchanged.
    /// </summary>
    public enum DamageType : byte
    {
        Normal = 0,
        Pierce = 1,
        Siege = 2,
        Magic = 3,
        Hero = 4,   // neutral placeholder until heroes ship (Epic 3)
        COUNT = 5
    }

    /// <summary>
    /// The armor classification of a unit. Integer values are STABLE keys (AR-26 / Story 1.6 AC1):
    /// Hero is inserted immediately before <see cref="COUNT"/> so every pre-existing value is unchanged.
    /// </summary>
    public enum ArmorType : byte
    {
        Unarmored = 0,
        Light = 1,
        Medium = 2,
        Heavy = 3,
        Fortified = 4, // Buildings
        Hero = 5,      // neutral placeholder until heroes ship (Epic 3)
        COUNT = 6
    }

    /// <summary>
    /// Data-driven damage multipliers (AR-26): final = base * Get(damageType, armorType).
    /// Replaces the retired hardcoded <c>static class DamageMatrix</c> — the float[,] a creator could
    /// not reach is now <c>resources/data/damage_table.json</c>, loaded via the <see cref="FixedJsonConverter"/>
    /// quantization boundary (AR-14) and baked into a dense <see cref="Fixed"/>[,] for integer-indexed,
    /// float-free in-tick lookup. 1.0 = full damage, 0.5 = half, 2.0 = double.
    /// </summary>
    public sealed class DamageTable
    {
        // Dense [(int)DamageType, (int)ArmorType] grid. The only damage-multiplier storage read in-tick.
        private readonly Fixed[,] _cells;

        private DamageTable(Fixed[,] cells) => _cells = cells;

        /// <summary>Returns the damage multiplier for a given damage/armor pair. Integer-indexed, no float.</summary>
        public Fixed Get(DamageType d, ArmorType a) => _cells[(int)d, (int)a];

        /// <summary>
        /// The canonical in-code table — built from the SAME float literals as the retired
        /// <c>DamageMatrix</c> static ctor, so the original 4x5 cells are bit-identical by construction
        /// (Story 1.6 AC1). The Hero row and Hero column are neutral 1.0 placeholders, tuned when heroes
        /// ship in Epic 3. Serves as the ctor fallback, the missing-file fallback, and the test oracle.
        /// The <see cref="Fixed.FromFloat"/> calls below are LOAD-TIME quantization (allowed), never in-tick.
        /// </summary>
        public static DamageTable Default { get; } = BuildDefault();

        private static DamageTable BuildDefault()
        {
            var c = new Fixed[(int)DamageType.COUNT, (int)ArmorType.COUNT];
            void Set(DamageType d, float un, float li, float me, float he, float fo, float hero)
            {
                c[(int)d, (int)ArmorType.Unarmored] = Fixed.FromFloat(un);
                c[(int)d, (int)ArmorType.Light]     = Fixed.FromFloat(li);
                c[(int)d, (int)ArmorType.Medium]    = Fixed.FromFloat(me);
                c[(int)d, (int)ArmorType.Heavy]     = Fixed.FromFloat(he);
                c[(int)d, (int)ArmorType.Fortified] = Fixed.FromFloat(fo);
                c[(int)d, (int)ArmorType.Hero]      = Fixed.FromFloat(hero);
            }
            //                       Unarmored Light  Medium Heavy  Fortified Hero
            Set(DamageType.Normal,   1.00f,   1.00f, 0.75f, 0.50f, 0.35f,    1.0f);
            Set(DamageType.Pierce,   1.50f,   1.00f, 0.75f, 0.35f, 0.25f,    1.0f);
            Set(DamageType.Siege,    0.50f,   0.50f, 1.00f, 1.00f, 1.50f,    1.0f);
            Set(DamageType.Magic,    1.00f,   1.00f, 1.00f, 1.00f, 0.50f,    1.0f);
            Set(DamageType.Hero,     1.0f,    1.0f,  1.0f,  1.0f,  1.0f,     1.0f);
            return new DamageTable(c);
        }

        /// <summary>Load-time DTO. The Dictionary exists ONLY here — it is baked into the dense
        /// <see cref="_cells"/> array immediately and never enumerated during a tick.</summary>
        private sealed class Dto
        {
            [JsonPropertyName("multipliers")]
            public Dictionary<DamageType, Dictionary<ArmorType, Fixed>>? Multipliers { get; set; }
        }

        // Mirror ScenarioSerializer's options shape: the FixedJsonConverter (AR-14 boundary, rejects
        // NaN/Inf/over-range), JsonStringEnumConverter (enum dictionary keys parse by NAME — an unknown
        // key throws a located JsonException), and Skip so damage_table.json may carry // comments.
        private static readonly JsonSerializerOptions _opts = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(), new FixedJsonConverter() },
        };

        /// <summary>Read a damage table from a JSON file on disk. Pass an absolute OS path
        /// (resolve res:// with <c>ProjectSettings.GlobalizePath</c> in the presentation layer).</summary>
        public static DamageTable Load(string absolutePath) => FromJson(File.ReadAllText(absolutePath));

        /// <summary>
        /// Deserialize + validate + bake a damage table from JSON text. Fails CLOSED with a located error
        /// (never a silent default): NaN/±Inf/over-range surface as a located <see cref="JsonException"/>
        /// from the <see cref="FixedJsonConverter"/>; a missing/extra row, wrong dimensions, or unknown enum
        /// key surface as a located <see cref="InvalidDataException"/> / <see cref="JsonException"/>.
        /// </summary>
        public static DamageTable FromJson(string json)
        {
            // NaN/Inf/over-range rejected here (located JsonException from FixedJsonConverter) — AC4 half #1.
            Dto? dto = JsonSerializer.Deserialize<Dto>(json, _opts);
            if (dto?.Multipliers is null)
                throw new InvalidDataException("damage_table: missing required 'multipliers' object.");

            var cells = new Fixed[(int)DamageType.COUNT, (int)ArmorType.COUNT];
            // Iterate by ENUM VALUE (deterministic, no Dictionary enumeration ever) — AC4 half #2.
            for (int d = 0; d < (int)DamageType.COUNT; d++)
            {
                if (!dto.Multipliers.TryGetValue((DamageType)d, out var row) || row is null)
                    throw new InvalidDataException($"damage_table: missing row '{(DamageType)d}'.");
                if (row.Count != (int)ArmorType.COUNT)
                    throw new InvalidDataException(
                        $"damage_table: row '{(DamageType)d}' has {row.Count} cells, expected {(int)ArmorType.COUNT}.");
                for (int a = 0; a < (int)ArmorType.COUNT; a++)
                {
                    if (!row.TryGetValue((ArmorType)a, out var v))
                        throw new InvalidDataException($"damage_table: missing cell [{(DamageType)d}][{(ArmorType)a}].");
                    cells[d, a] = v;
                }
            }
            if (dto.Multipliers.Count != (int)DamageType.COUNT)
                throw new InvalidDataException(
                    $"damage_table: {dto.Multipliers.Count} rows, expected {(int)DamageType.COUNT} (unknown damage type?).");
            return new DamageTable(cells);
        }
    }
}
