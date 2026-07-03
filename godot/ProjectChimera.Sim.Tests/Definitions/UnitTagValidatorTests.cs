#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.11 (AC2) — the closed-set tag validator. A unit whose <c>tags</c> carries a token outside
    /// {Organic, Mechanical, Magical} is rejected with a LOCATED error (naming the unit id AND the offending token) and
    /// DROPPED from the faction — so it can never spawn (<c>GetUnit</c> returns null → the applier's <c>def == null</c>
    /// skip runs → no EntityWorld slot). A valid unit passes untouched; the rest of the faction still loads. Godot-free.
    /// </summary>
    public class UnitTagValidatorTests
    {
        private static UnitDefinition Unit(string id, params string[] tags) =>
            new UnitDefinition { Id = id, Tags = tags.Length == 0 ? null : tags };

        private static FactionDefinition Faction(params UnitDefinition[] units)
        {
            var f = new FactionDefinition();
            foreach (UnitDefinition u in units) f.Units.Add(u);
            return f;
        }

        [Fact]
        public void UnknownTag_IsRejected_WithLocatedError_NamingUnitAndToken()   // AC2.2
        {
            FactionDefinition faction = Faction(Unit("render_crawler", "Undead"));

            var errors = UnitTagValidator.ValidateAndDropUnits(faction);

            string err = Assert.Single(errors);
            Assert.Contains("render_crawler", err); // names the unit id
            Assert.Contains("Undead", err);         // names the offending token
        }

        [Fact]
        public void RejectedUnit_IsDropped_NoSpawnableEntity()   // AC2.3 — GetUnit null → the applier skips → no Create
        {
            FactionDefinition faction = Faction(Unit("bad", "Undead"));

            UnitTagValidator.ValidateAndDropUnits(faction);

            Assert.Null(faction.GetUnit("bad")); // dropped → ScenarioApplier's def==null skip → no EntityWorld slot
            Assert.Empty(faction.Units);
        }

        [Fact]
        public void ValidTags_Pass_Untouched()   // positive control — the whole closed set is accepted
        {
            FactionDefinition faction = Faction(Unit("ok", "Organic", "Mechanical", "Magical"));

            var errors = UnitTagValidator.ValidateAndDropUnits(faction);

            Assert.Empty(errors);
            Assert.NotNull(faction.GetUnit("ok"));
        }

        [Fact]
        public void UntaggedUnit_Passes()   // AC5 back-compat — no tags is valid
        {
            FactionDefinition faction = Faction(Unit("plain"));

            Assert.Empty(UnitTagValidator.ValidateAndDropUnits(faction));
            Assert.NotNull(faction.GetUnit("plain"));
        }

        [Fact]
        public void OnlyOffenders_AreDropped_RestOfFactionStillLoads()   // AC2.3 + enumeration-safety (multi-offender RemoveAll)
        {
            FactionDefinition faction = Faction(
                Unit("good1", "Organic"),
                Unit("bad1", "Undead"),
                Unit("good2", "Mechanical"),
                Unit("bad2", "Zombie"));

            var errors = UnitTagValidator.ValidateAndDropUnits(faction);

            Assert.Equal(2, errors.Count);
            Assert.Null(faction.GetUnit("bad1"));
            Assert.Null(faction.GetUnit("bad2"));
            Assert.NotNull(faction.GetUnit("good1")); // survivors keep loading
            Assert.NotNull(faction.GetUnit("good2"));
        }

        [Fact]
        public void CaseSensitive_LowercaseToken_IsRejected()   // teeth: exact-case closed set (mirrors ParsedTags)
        {
            FactionDefinition faction = Faction(Unit("u", "organic")); // lowercase is NOT in the closed set

            Assert.Single(UnitTagValidator.ValidateAndDropUnits(faction));
            Assert.Null(faction.GetUnit("u"));
        }
    }
}
