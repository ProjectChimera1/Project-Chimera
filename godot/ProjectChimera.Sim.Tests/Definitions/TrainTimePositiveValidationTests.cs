#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-481 — <c>train_time</c> is now STRICTLY positive for anything that can sit in a production queue, and keeps
    /// the generic <c>[0, 32768)</c> bound only for a <c>Structure</c> (which is BUILT via <c>construction_time</c>,
    /// never enqueued — every shipped building authors <c>train_time: 0</c>).
    ///
    /// <para><b>The defect.</b> <c>BuildingSystem.TrainUnit</c> seeds the head slot's <c>ProductionTimer</c> from
    /// <c>def.TrainTime</c>. A unit authored with <c>train_time &lt;= 0</c> therefore produces a NON-EMPTY head whose
    /// timer is already expired: the production tick treats it as instantly complete, so it is never actually trained
    /// over time and the whole depth-5 queue behind it drains at one unit per tick. (Before the DW-479 tick fix the
    /// same state was skipped forever instead, freezing every order behind it — either way the authored value is
    /// broken, which is why the fix belongs at the authoring gate.) No syntactic rule caught it: the generic
    /// <c>CheckStat</c> bound is INCLUSIVE of 0.</para>
    ///
    /// <para>Same shape as the DW-380 <c>attack_speed</c>/<c>mesh_scale</c>/<c>collision_radius</c> precedent — a
    /// strictly-positive lower bound only where zero is degenerate — so every consumer of the shared gate inherits it:
    /// hand-authored Unit Card edits, the Story-8.5 balance-apply path, AI unit drafts, and (via reuse)
    /// <see cref="BuildingDefinitionValidator"/>.</para>
    ///
    /// <para><b>Determinism.</b> An authoring-time REJECT rule only: no stat value, quantization, or SoA write
    /// changes, so no <c>SimChecksum</c>/<c>ContentHash</c>/<c>StartStateHash</c> input moves. Every shipped unit in
    /// alpha_faction.json / beta_faction.json authors a positive train_time, and every shipped <c>train_time: 0</c>
    /// belongs to a <c>Structure</c> — proved by <see cref="ShippedBuildingPosture_ZeroTrainTimeOnAStructure_StaysValid"/>.</para>
    /// </summary>
    public class TrainTimePositiveValidationTests
    {
        private static readonly UnitDefinitionValidator UV = new();

        /// <summary>A fully-valid minimal trainable unit; each case mutates exactly one field away from it.</summary>
        private static UnitDefinition ValidUnit() => new UnitDefinition
        {
            Id = "grunt", DisplayName = "Grunt", Category = "Melee",
            Hp = 100f, Speed = 4f, AttackDamage = 10f, AttackRange = 1.5f, AttackSpeed = 1f,
            DamageType = "Normal", ArmorType = "Unarmored", SeparationPriority = "Normal",
            CostOre = 50, CostCrystal = 0, Supply = 1, VisionRange = 8f,
            Armor = 0f, TrainTime = 8f, SplashRadius = 0f, CollisionRadius = 1f, MeshScale = 1f, MaxEnergy = 0f,
        };

        private static UnitValidationResult Run(UnitDefinition def) =>
            UV.Validate(def, registry: null, siblings: null);

        private static void AssertSingleErrorOn(UnitValidationResult r, string fieldPath, string id)
        {
            Assert.False(r.Ok, "expected a reject, got Ok");
            List<(string FieldPath, string Message)> hits =
                r.Errors.Where(e => e.FieldPath == fieldPath).ToList();
            Assert.True(hits.Count == 1,
                $"expected exactly ONE error on '{fieldPath}' (a doubled badge is a bug), got {hits.Count}: " +
                string.Join(" | ", r.Errors.Select(e => e.FieldPath)));
            Assert.Contains(id, hits[0].Message);
            Assert.Contains(fieldPath, hits[0].Message);
        }

        [Fact]
        public void ZeroTrainTime_OnATrainableUnit_IsRejected()
        {
            // RED without the fix: CheckStat's [0, Range) admits 0, and TrainUnit then queues a head whose timer is
            // already expired — a production order that is never actually trained over time.
            var def = ValidUnit(); def.TrainTime = 0f;
            AssertSingleErrorOn(Run(def), "train_time", "grunt");
        }

        [Theory]
        [InlineData("Worker")]
        [InlineData("Melee")]
        [InlineData("Ranged")]
        [InlineData("Siege")]
        [InlineData("Air")]
        public void ZeroTrainTime_IsRejectedForEveryTrainableArchetype(string category)
        {
            // Every one of these categories is reachable as a producer's `produces_category`, so all five can land in
            // a production queue. Only Structure is exempt.
            var def = ValidUnit(); def.Category = category; def.TrainTime = 0f;
            AssertSingleErrorOn(Run(def), "train_time", "grunt");
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.NegativeInfinity)]
        [InlineData(32768f)]   // == the 16.16 ceiling (exclusive)
        public void OutOfRangeTrainTime_ReportsExactlyOneBadge_NotTwo(float value)
        {
            // Exactly ONE of the two rules runs per def (strictly-positive for trainables, generic for Structures),
            // so a value that is both non-positive AND out of range still badges the control once — the per-field-
            // badge contract (D-9).
            var def = ValidUnit(); def.TrainTime = value;
            AssertSingleErrorOn(Run(def), "train_time", "grunt");
        }

        [Fact]
        public void ShippedBuildingPosture_ZeroTrainTimeOnAStructure_StaysValid()
        {
            // Every shipped building (command_center / barracks / archery_range / siege_workshop / aviary in both
            // alpha_faction.json and beta_faction.json) authors category "Structure" with train_time 0 — a building
            // is BUILT (construction_time), never enqueued, so 0 is the correct authoring there. Making the rule
            // unconditional would reject all shipped content.
            var def = ValidUnit();
            def.Category = "Structure";
            def.AttackDamage = 0f; def.AttackSpeed = 0f; def.AttackRange = 0f; def.Speed = 0f;
            def.VisionRange = 0f; def.TrainTime = 0f; def.Supply = 0;
            UnitValidationResult r = Run(def);
            Assert.True(r.Ok, string.Join(" | ", r.Errors.Select(e => e.Message)));
        }

        [Fact]
        public void StructureWithNegativeTrainTime_IsStillRejected_ByTheGenericBound()
        {
            // The Structure exemption relaxes the LOWER bound to zero-inclusive, not to "anything" — a negative or
            // non-finite value is still unrepresentable once quantized and must still fail closed.
            var def = ValidUnit(); def.Category = "Structure"; def.TrainTime = -1f;
            AssertSingleErrorOn(Run(def), "train_time", "grunt");
        }

        [Fact]
        public void PositiveTrainTime_StaysValid_OnBothArms()
        {
            var unit = ValidUnit(); unit.TrainTime = 0.5f;
            Assert.True(Run(unit).Ok);

            var structure = ValidUnit(); structure.Category = "Structure"; structure.TrainTime = 12f;
            Assert.DoesNotContain(Run(structure).Errors, e => e.FieldPath == "train_time");
        }

        [Fact]
        public void ShippedBuildingDefinitions_WithZeroTrainTime_StillPassTheBuildingGate()
        {
            // BuildingDefinitionValidator reuses this whole gate kinded "building"; the shipped posture (Structure +
            // train_time 0) must survive that route too, not just the direct unit route.
            var b = new BuildingDefinition
            {
                Id = "barracks", DisplayName = "Barracks", Category = "Structure",
                Hp = 300f, Speed = 0f, AttackDamage = 0f, AttackRange = 0f, AttackSpeed = 0f,
                DamageType = "Normal", ArmorType = "Fortified", SeparationPriority = "Normal",
                CostOre = 100, CostCrystal = 0, Supply = 0, VisionRange = 10f,
                Armor = 0f, TrainTime = 0f, SplashRadius = 0f, CollisionRadius = 1f, MeshScale = 1f, MaxEnergy = 0f,
                ConstructionTime = 10f, SupplyBonus = 0,
            };
            BuildingValidationResult r = BuildingDefinitionValidator.Validate(b, siblings: null);
            Assert.DoesNotContain(r.Errors, e => e.FieldPath == "train_time");
        }
    }
}
