#nullable enable
using System.Text.Json;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.11 (AC1 / AC5) — the UnitTag data model. <c>tags</c> deserializes through the LENIENT faction loader to a
    /// distinct <see cref="UnitTag"/> axis; <see cref="UnitDefinition.ParsedTags"/> OR-folds recognized tokens, defaults
    /// to <see cref="UnitTag.None"/> (NOT an <c>All</c>-style fallback like <c>ParsedAttackDomains</c>), and is
    /// independent of DamageType / ArmorType / Category. Godot-free.
    /// </summary>
    public class UnitTagDataModelTests
    {
        private static UnitDefinition Load(string json) =>
            JsonSerializer.Deserialize<UnitDefinition>(json, FactionDefinition.JsonOptions)!;

        [Fact]
        public void MultipleTags_OrFoldToTheUnion()   // AC1.2
        {
            Assert.Equal(UnitTag.Organic | UnitTag.Magical, Load("""{ "id": "u", "tags": ["Organic", "Magical"] }""").ParsedTags);
        }

        [Fact]
        public void SingleTag_Resolves()
        {
            Assert.Equal(UnitTag.Mechanical, Load("""{ "id": "u", "tags": ["Mechanical"] }""").ParsedTags);
        }

        [Fact]
        public void NoTagsField_DefaultsToNone_NotAll()   // AC5.1 — the critical divergence from ParsedAttackDomains
        {
            UnitDefinition def = Load("""{ "id": "u" }""");
            Assert.Null(def.Tags);                     // unauthored (null), distinguishable from authored-empty
            Assert.Equal(UnitTag.None, def.ParsedTags);
        }

        [Fact]
        public void EmptyTagsArray_IsNone()   // AC5.1 — authored-empty also folds to None (not All)
        {
            Assert.Equal(UnitTag.None, Load("""{ "id": "u", "tags": [] }""").ParsedTags);
        }

        [Fact]
        public void UnknownToken_LenientlyFoldsToNone_TheValidatorRejects()   // AC2.4 — accessor is lenient; validator rejects
        {
            Assert.Equal(UnitTag.None, Load("""{ "id": "u", "tags": ["Undead"] }""").ParsedTags);
            // A known+unknown mix keeps ONLY the known bit (the unknown contributes None).
            Assert.Equal(UnitTag.Organic, Load("""{ "id": "u", "tags": ["Organic", "Undead"] }""").ParsedTags);
        }

        [Fact]
        public void Tags_AreADistinctAxis_FromDamageArmorCategory()   // AC1.3 — four independent axes
        {
            UnitDefinition def = Load(
                """{ "id": "u", "tags": ["Magical"], "damage_type": "Pierce", "armor_type": "Heavy", "category": "Air" }""");
            Assert.Equal(UnitTag.Magical, def.ParsedTags);
            Assert.Equal(DamageType.Pierce, def.ParsedDamageType);
            Assert.Equal(ArmorType.Heavy, def.ParsedArmorType);
            Assert.Equal(UnitCategory.Air, def.ParsedCategory);
        }

        [Fact]
        public void TaggedDef_LoadsCleanThroughTheLenientLoader()   // AC1 — the faction path never throws on tags
        {
            Assert.Null(Record.Exception(() => Load("""{ "id": "u", "tags": ["Organic", "Mechanical", "Magical"] }""")));
        }
    }
}
