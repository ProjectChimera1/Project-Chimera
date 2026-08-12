#nullable enable
using System;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-694 + DW-695 — the reserved-device rule's WORDING and its fourth authoring surface.
    ///
    /// <para><b>DW-694 — the shipped message overclaimed.</b> DW-454 justified the reject with a local crash: "the
    /// filesystem rejects '&lt;id&gt;.json' as a file name", surfacing as an opaque "Save failed". Probed empirically
    /// while scoping DW-528, that is FALSE on this project's primary platform (Win11 26200): <c>File.WriteAllText</c>
    /// creates <c>con.json</c>, <c>con.json.tmp</c> and bare <c>con</c>, and so does <c>cmd.exe</c> — and DW-528's
    /// without-the-fix RED run confirmed it (Save reported SUCCESS and wrote the file). A reject that promises a
    /// crash the creator will never see is worse than no explanation: the first time it is doubted it looks wrong,
    /// and the REAL reason goes unsaid. The guards are right and stay — authored content is meant to be SHARED, and
    /// such a file is unopenable wherever the reservation IS enforced — so this is a wording correction, never a
    /// revert. These tests pin the portability framing so it cannot silently rot back.</para>
    ///
    /// <para><b>DW-695 — the ability editor was the uncovered fourth surface.</b> DW-454 wired the rule into the item
    /// sim gate, the item editor gate and the unit/building gate; DW-528 added the filename-level companion for the
    /// faction wizard. <c>AbilityEditorPanel</c> was covered by none of them and <c>AbilityValidator</c> had no
    /// equivalent rule — and here the case is strictly WORSE than the wizard's, because the panel writes
    /// <c>{SanitizeId(def.Id)}.json</c> with no <c>_faction</c>-style suffix decorating the basename: the reserved
    /// word IS the whole basename before the first dot. The rule now lives on the SIM validator, so the panel's
    /// validate-gated Save inherits it and the content-load path rejects it too.</para>
    ///
    /// <para>Godot-free: every gate under test is a pure validator. No filesystem access — this suite asserts what
    /// the gates SAY, never what a particular Windows build does with a file.</para>
    /// </summary>
    public class ReservedDeviceNamePortabilityTests
    {
        public static TheoryData<string> ReservedIds() => new() { "con", "prn", "aux", "nul", "com1", "lpt9" };

        // ── DW-694 — one sentence, four gates, portability framing ────────────────────────────────────────

        [Fact]
        public void TheRejectMessage_ExplainsPortability_NotALocalWriteFailure()
        {
            string msg = UnitDefinitionValidator.ReservedDeviceNameMessage("con");

            // The correction: what the creator is told must be the thing that is actually true.
            Assert.Contains("cannot be opened", msg, StringComparison.Ordinal);
            Assert.Contains("reservation is enforced", msg, StringComparison.Ordinal);
            Assert.Contains("'con.json'", msg, StringComparison.Ordinal); // names the file they would produce

            // And the overclaim must be gone. RED before DW-694: the shipped text said exactly this.
            Assert.DoesNotContain("the filesystem rejects", msg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Save failed", msg, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheMessageStillNamesTheSharedReservedList_SoTheFourGatesCannotDrift()
        {
            Assert.Contains(UnitDefinitionValidator.ReservedPipeList,
                UnitDefinitionValidator.ReservedDeviceNameMessage("nul"), StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(ReservedIds))]
        public void EveryGate_QuotesTheOneSharedSentence(string id)
        {
            // Three hand-maintained copies of one sentence drift — the item gates used to carry their own literal,
            // pipe list and all. Each gate's badge must now BE the shared sentence, verbatim.
            string expected = UnitDefinitionValidator.ReservedDeviceNameMessage(id);

            var unit = new UnitDefinitionValidator().Validate(ValidUnit(id), null, null);
            Assert.Contains(expected, unit.Errors.Single(e => e.FieldPath == "id").Message, StringComparison.Ordinal);

            ItemValidationResult itemSim = new ItemDefinitionValidator().Validate(new ItemDefinition { Id = id, Charges = 0 });
            Assert.Contains(expected, itemSim.Error!, StringComparison.Ordinal);

            ItemValidationResult itemFields = new ItemDefinitionValidator().ValidateFields(new ItemDefinition { Id = id, Charges = 0 });
            Assert.Contains(expected, itemFields.Errors.Single(e => e.FieldPath == "id").Message, StringComparison.Ordinal);

            AbilityValidationResult ability = new AbilityValidator().Validate(ValidAbility(id));
            Assert.Contains(expected, ability.Error!, StringComparison.Ordinal);
        }

        // ── DW-695 — the ability gate ─────────────────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(ReservedIds))]
        public void ReservedAbilityId_IsRejected_ByTheSimGate(string id)
        {
            // RED without the fix: AbilityValidator only checked null/empty, so `con` minted a Validated<> and
            // AbilityEditorPanel wrote a literal `con.json` — the reserved word as the WHOLE basename, with no
            // suffix to save it the way the faction wizard's `_faction` accidentally did.
            AbilityValidationResult r = new AbilityValidator().Validate(ValidAbility(id));

            Assert.False(r.Ok);
            Assert.Contains($"ability '{id}'.id", r.Error!, StringComparison.Ordinal); // LOCATED on the id field
            Assert.Contains("reserved device name", r.Error!, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("console")]      // merely CONTAINS a reserved word
        [InlineData("con_2")]        // the suffixed mint MakeUniqueId hands back
        [InlineData("com0")]         // not a reserved device
        [InlineData("fireball")]     // a shipped id
        [InlineData("nullify")]
        public void ANonReservedAbilityId_StillValidates(string id)
        {
            // The over-rejection control: this rule must not cost creators ordinary names. Without it the gate could
            // be "reject everything containing con" and every assertion above would still pass.
            AbilityValidationResult r = new AbilityValidator().Validate(ValidAbility(id));
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void EveryShippedAbilityId_SurvivesTheNewRule()
        {
            // A new content gate that rejects shipped content is a build break, not a guard. Cheap to prove.
            foreach (string id in new[]
                     {
                         "aura_guard", "battle_fury", "blink_strike", "fireball", "furnace_pour", "furnace_trickle",
                         "ground_nuke", "matter_infusion", "mend_ally", "mend_matter", "minor_heal",
                         "onhit_searing", "spike_transmutation",
                     })
                Assert.False(UnitDefinitionValidator.IsReservedDeviceName(id), id);
        }

        [Fact]
        public void TheAbilityRule_IsTheSameConventionAsTheOtherThree_NotACopy()
        {
            // The DW-454/DW-528 lesson: one convention helper, not four hand-kept tables. If someone re-literalizes
            // the ability arm, this catches the drift the moment the shared set changes.
            foreach (string id in new[] { "con", "prn", "aux", "nul", "com5", "lpt1", "console", "com0" })
            {
                bool shared = UnitDefinitionValidator.IsReservedDeviceName(id);
                bool abilityRejects = !new AbilityValidator().Validate(ValidAbility(id)).Ok;
                Assert.Equal(shared, abilityRejects);
            }
        }

        // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The minimal ability that passes every OTHER rule, so a failure can only be the id.</summary>
        private static AbilityDefinition ValidAbility(string id) => new()
        {
            Id = id, DisplayName = "T", Targeting = "Self", Activation = "active",
            EffectGraph = new HealEffect(Fixed.FromInt(5)),
        };

        /// <summary>The minimal unit that passes every OTHER rule (mirrors DefinitionValidatorStrictBoundsTests').</summary>
        private static UnitDefinition ValidUnit(string id) => new()
        {
            Id = id, DisplayName = "Grunt", Category = "Melee",
            Hp = 100f, Speed = 4f, AttackDamage = 10f, AttackRange = 1.5f, AttackSpeed = 1f,
            DamageType = "Normal", ArmorType = "Unarmored", SeparationPriority = "Normal",
            CostOre = 50, CostCrystal = 0, Supply = 1, VisionRange = 8f,
            Armor = 0f, TrainTime = 8f, SplashRadius = 0f, CollisionRadius = 1f, MeshScale = 1f, MaxEnergy = 0f,
        };
    }
}
