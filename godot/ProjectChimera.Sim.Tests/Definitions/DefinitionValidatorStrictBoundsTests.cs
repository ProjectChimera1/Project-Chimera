#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-380 + DW-454 — the two permissiveness holes the shared authoring gates carried, each with a positive control
    /// that would still pass WITHOUT the fix and a negative control that is demonstrably RED without it (A3, "every gate
    /// ships with teeth").
    ///
    /// <para><b>DW-380.</b> <see cref="UnitDefinitionValidator"/>'s generic <c>CheckStat</c> bound is <c>[0, 32768)</c> —
    /// INCLUSIVE of 0 — which admitted <c>attack_speed=0</c> (the shot clock re-arms already expired ⇒ attacks EVERY
    /// tick), <c>mesh_scale=0</c> (invisible unit) and <c>collision_radius=0</c> (silently rewritten to the engine
    /// default at spawn, so the saved value is not the simulated one). All three are now strictly-positive where zero is
    /// degenerate, and the rule is shared by every consumer of the gate — hand-authored edits, the Story-8.5
    /// <see cref="ProjectChimera.AI.BalanceSuggestionApplier"/> apply path, AI drafts, and (via reuse)
    /// <see cref="BuildingDefinitionValidator"/>.</para>
    ///
    /// <para><b>DW-454.</b> The <c>[a-z0-9_]</c> charset check ADMITS every Win32 reserved device basename (<c>con</c>,
    /// <c>nul</c>, <c>com1</c>, …), so an item named <c>con</c> passed validation and then made
    /// <c>ItemCardPanel.Persist()</c>'s <c>File.WriteAllText("con.json.tmp")</c> throw on Windows — an opaque generic
    /// "Save failed" with no field badge and no way to save. Now rejected on BOTH item surfaces and on the sibling
    /// unit/building gate, and the shared id minter can no longer hand back a reserved id.</para>
    ///
    /// <para><b>DW-528.</b> DW-454's helper compares a WHOLE id, which only fits a caller that uses the id verbatim as
    /// the file basename. The faction wizard decorates it (<c>&lt;id&gt;_faction.json</c>), and Win32 reserves the
    /// segment before the FIRST dot — so the decoration makes a bare <c>con</c> harmless while an id of <c>con.x</c>
    /// still resolves to the CON device. <c>IsReservedDeviceFileName</c> is the filename-level companion that gets
    /// both cases right; its faction-wizard wiring is covered in <c>FactionDefinerWizardTests</c>.</para>
    ///
    /// <para><b>DW-527.</b> <c>hp</c> is the FOURTH degenerate-at-zero stat: the unit gate ran it through the same
    /// inclusive-of-zero <c>CheckStat</c> bound, so a 0-hp unit was authorable — it spawns ALIVE at 0 HP with a zero
    /// health ceiling, can never be healed (<c>ModifierStore</c> clamps Health into <c>[0, EffectiveMaxHealth]</c>) and
    /// dies to the first point of damage. Buildings were protected by <see cref="BuildingDefinitionValidator"/>'s own
    /// <c>hp &lt;= 0</c> branch — but since that validator ALSO reuses the unit gate, a negative/NaN building hp badged
    /// one control twice, which is why closing this meant reconciling the two hp checks into a single rule (the shared
    /// gate owns the value; the building side keeps only its <c>HpAuthored</c> presence check) rather than just
    /// tightening the bound.</para>
    ///
    /// <para><b>Determinism.</b> These are authoring-time REJECT rules only: no stat value, quantization, or SoA write
    /// changes, so no <c>SimChecksum</c>/<c>ContentHash</c>/<c>StartStateHash</c> input moves. The shipped factions
    /// authored none of the newly-rejected values (proved by <see cref="ShippedNonCombatantPosture_StaysValid"/>'s
    /// zero-damage/zero-interval case, the only zero any shipped entity authors for these fields, and by
    /// <see cref="ShippedBuildingAndUnitPostures_StayValid_UnderTheHpRule"/> for hp).</para>
    /// </summary>
    public class DefinitionValidatorStrictBoundsTests
    {
        private static readonly UnitDefinitionValidator UV = new();

        /// <summary>A fully-valid minimal unit; each case mutates exactly one field away from it.</summary>
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

        // ── DW-380 (a): attack_speed — strictly positive ONLY when the entity actually deals damage ───────────────

        [Fact]
        public void ZeroAttackSpeed_OnDamageDealer_IsRejected()
        {
            // RED without the fix: CheckStat's [0, Range) admits 0, and CombatSystem then sets
            // AttackCooldown = AttackSpeed = 0 on every shot ⇒ the unit fires every tick, unbounded DPS.
            var def = ValidUnit(); def.AttackSpeed = 0f;
            AssertSingleErrorOn(Run(def), "attack_speed", "grunt");
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(32768f)]   // == the 16.16 ceiling (exclusive)
        public void OutOfRangeAttackSpeed_ReportsExactlyOneBadge_NotTwo(float value)
        {
            // A negative interval is non-positive AND out of range, so it satisfies BOTH the generic rule and the new
            // strictly-positive one. It must badge the field ONCE — the per-field-badge contract (D-9) means one control
            // gets one message. This case was RED on the first implementation pass (two 'attack_speed' errors), which is
            // why the strict rule is short-circuited on the generic rule's result rather than evaluated independently.
            var def = ValidUnit(); def.AttackSpeed = value;
            UnitValidationResult r = Run(def);
            AssertSingleErrorOn(r, "attack_speed", "grunt");
            // It is the generic range message that fires, not the strict-positive one.
            Assert.Contains("must be finite and in [0,", r.Errors.First(e => e.FieldPath == "attack_speed").Message);
        }

        [Fact]
        public void ShippedNonCombatantPosture_StaysValid()
        {
            // Every shipped building (command_center / barracks / archery_range / siege_workshop / aviary in both
            // alpha_faction.json and beta_faction.json) authors attack_speed 0 AND attack_damage 0 — a 0-damage attack
            // is a no-op whose cadence is irrelevant. Gating the strict rule on attack_damage keeps that posture legal;
            // gating it on the archetype (or making it unconditional) would reject all shipped content.
            var def = ValidUnit();
            def.Category = "Structure";
            def.AttackDamage = 0f; def.AttackSpeed = 0f; def.AttackRange = 0f; def.Speed = 0f;
            def.VisionRange = 0f; def.TrainTime = 0f; def.Supply = 0;
            UnitValidationResult r = Run(def);
            Assert.True(r.Ok, string.Join(" | ", r.Errors.Select(e => e.Message)));
        }

        [Fact]
        public void ZeroAttackSpeed_WithZeroDamage_ThenGivenDamage_FlipsToRejected()
        {
            // The conditional's teeth: the SAME attack_speed=0 def flips from valid to rejected the moment real damage
            // is authored — i.e. the rule tracks "does this entity attack", not the archetype string.
            var def = ValidUnit(); def.AttackSpeed = 0f; def.AttackDamage = 0f;
            Assert.True(Run(def).Ok);

            def.AttackDamage = 1f;   // now a real attacker with a 0 interval
            AssertSingleErrorOn(Run(def), "attack_speed", "grunt");
        }

        // ── DW-380 (b): mesh_scale / collision_radius — unconditionally strictly positive ──────────────────────────

        [Fact]
        public void ZeroMeshScale_IsRejected()
        {
            // RED without the fix: the renderers apply def.MeshScale verbatim with no zero guard ⇒ an invisible unit
            // that still fights. mesh_scale is EXCLUDED from ContentHash, so this reject moves no hash.
            var def = ValidUnit(); def.MeshScale = 0f;
            AssertSingleErrorOn(Run(def), "mesh_scale", "grunt");
        }

        [Fact]
        public void ZeroCollisionRadius_IsRejected()
        {
            // RED without the fix: EntityWorld.ClampCollisionRadius silently rewrites <= 0 to the engine default at
            // spawn, so the author saves 0 and the simulation (and the checksum) uses 1.0 with no feedback.
            var def = ValidUnit(); def.CollisionRadius = 0f;
            AssertSingleErrorOn(Run(def), "collision_radius", "grunt");
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(32768f)]   // == the 16.16 ceiling (exclusive)
        public void NonFiniteOrOutOfRangeMeshScale_IsStillRejected(float value)
        {
            // The strictly-positive helper must keep the pre-existing finite/upper-bound teeth, not replace them.
            var def = ValidUnit(); def.MeshScale = value;
            AssertSingleErrorOn(Run(def), "mesh_scale", "grunt");
        }

        [Fact]
        public void SmallPositiveMeshScaleAndCollisionRadius_AreValid()
        {
            // The bound is strictly positive, NOT "at least 1" — a deliberately tiny scale/radius stays authorable.
            var def = ValidUnit(); def.MeshScale = 0.05f; def.CollisionRadius = 0.1f; def.AttackSpeed = 0.05f;
            UnitValidationResult r = Run(def);
            Assert.True(r.Ok, string.Join(" | ", r.Errors.Select(e => e.Message)));
        }

        [Fact]
        public void BuildingGate_InheritsTheStrictBounds_KindedBuilding()
        {
            // BuildingDefinitionValidator reuses the unit gate kinded "building", so a defensive tower authoring real
            // damage with a 0 interval fails closed there too, and the message names the BUILDING.
            var def = new BuildingDefinition
            {
                Id = "guard_tower", DisplayName = "Guard Tower", Category = "Structure",
                Hp = 400f, ConstructionTime = 20f, SupplyBonus = 0, ProducesCategory = "None",
                AttackDamage = 15f, AttackSpeed = 0f, AttackRange = 6f,
            };
            BuildingValidationResult r = BuildingDefinitionValidator.Validate(def);
            Assert.False(r.Ok);
            (string FieldPath, string Message) e = r.Errors.First(x => x.FieldPath == "attack_speed");
            Assert.Contains("building 'guard_tower'", e.Message);
        }

        // ── DW-380 (c): the Story-8.5 balance-apply path is gated by the SAME rules ────────────────────────────────

        [Theory]
        [InlineData("attack_speed")]
        [InlineData("mesh_scale")]
        [InlineData("collision_radius")]
        public void BalanceApply_ProposingZero_IsRejected_AndLeavesTargetUntouched(string field)
        {
            // The originating defect (DW-380 came out of the Story 8.5 review): a balance suggestion made reaching these
            // degenerate values ONE CLICK, because TryApply gates the proposal through this very validator.
            // DW-382 note: mesh_scale has since been dropped from the tunable vocabulary entirely, so its row now
            // rejects even EARLIER (the not-tunable gate) — every assertion below still holds.
            var target = ValidUnit();
            var (candidate, err) = ProjectChimera.AI.BalanceSuggestionApplier.TryApply(target, field, 0d, null);

            Assert.Null(candidate);
            Assert.NotNull(err);
            Assert.Contains(field, err!);
            // The original is never mutated by a rejected apply.
            Assert.Equal(1f, target.AttackSpeed);
            Assert.Equal(1f, target.MeshScale);
            Assert.Equal(1f, target.CollisionRadius);
        }

        // ── DW-454: reserved Windows device basenames ─────────────────────────────────────────────────────────────

        public static IEnumerable<object[]> ReservedNames() => new[]
        {
            new object[] { "con" }, new object[] { "prn" }, new object[] { "aux" }, new object[] { "nul" },
            new object[] { "com1" }, new object[] { "com9" }, new object[] { "lpt1" }, new object[] { "lpt9" },
        };

        [Theory]
        [MemberData(nameof(ReservedNames))]
        public void ReservedBasename_IsRecognised(string id)
        {
            Assert.True(UnitDefinitionValidator.IsReservedDeviceName(id), id);
            // The charset gate alone lets every one of them through — that is exactly why the extra rule is needed.
            Assert.Equal(id, UnitDefinitionValidator.SanitizeId(id));
        }

        [Theory]
        [InlineData("console")]     // merely CONTAINS "con"
        [InlineData("con_2")]       // the suffixed mint
        [InlineData("nullify")]
        [InlineData("com0")]        // not a reserved device
        [InlineData("lpt0")]
        [InlineData("com10")]
        [InlineData("grunt")]
        public void NonReservedName_IsNotFlagged(string id)
        {
            Assert.False(UnitDefinitionValidator.IsReservedDeviceName(id), id);
        }

        [Theory]
        [MemberData(nameof(ReservedNames))]
        public void ReservedItemId_IsRejected_BySimGate(string id)
        {
            // RED without the fix: the charset check passes, Validate mints a Validated<ItemDefinition>, and Persist()
            // writes "<id>.json" onto a Win32 DOS-device name. DW-694 corrected the SYMPTOM recorded here: the write
            // does NOT throw on this project's Windows build (measured — see UnitDefinitionValidator's
            // _reservedBasenames doc); the cost is that the shared artifact is unopenable wherever the reservation IS
            // enforced. The reject stands; only the reason it gives does.
            ItemValidationResult r = new ItemDefinitionValidator().Validate(
                new ItemDefinition { Id = id, Charges = 0 });
            Assert.False(r.Ok);
            Assert.Contains("id", r.Error!);
            Assert.Contains("reserved device name", r.Error!);
        }

        [Theory]
        [MemberData(nameof(ReservedNames))]
        public void ReservedItemId_IsRejected_ByEditorFieldsGate(string id)
        {
            // ValidateFields is the gate that actually blocks Persist() (DoSave→Revalidate keeps Save disabled), so the
            // badge MUST appear here or the creator saves a file that only breaks for whoever they share it with
            // (DW-694: the local write succeeds on this platform, which is precisely why an author would never notice).
            ItemValidationResult r = new ItemDefinitionValidator().ValidateFields(
                new ItemDefinition { Id = id, Charges = 0 });
            Assert.False(r.Ok);
            List<(string FieldPath, string Message)> hits = r.Errors.Where(e => e.FieldPath == "id").ToList();
            Assert.True(hits.Count == 1, $"expected exactly one 'id' badge, got {hits.Count}");
            Assert.Contains("reserved device name", hits[0].Message);
        }

        [Fact]
        public void UppercaseReservedItemId_ReportsExactlyOneIdBadge_NotTwo()
        {
            // "CON" fails the charset rule AND is reserved. Both must not badge the same field twice (the else-if
            // chain): a doubled badge on one control is a UX regression the per-field-badge design forbids.
            ItemValidationResult r = new ItemDefinitionValidator().ValidateFields(
                new ItemDefinition { Id = "CON", Charges = 0 });
            Assert.Equal(1, r.Errors.Count(e => e.FieldPath == "id"));
        }

        [Theory]
        [MemberData(nameof(ReservedNames))]
        public void ReservedUnitId_IsRejected(string id)
        {
            var def = ValidUnit(); def.Id = id;
            AssertSingleErrorOn(Run(def), "id", id);
            Assert.Contains("reserved device name", Run(def).Errors.First(e => e.FieldPath == "id").Message);
        }

        [Fact]
        public void UppercaseReservedUnitId_ReportsExactlyOneIdBadge_NotTwo()
        {
            var def = ValidUnit(); def.Id = "CON";
            AssertSingleErrorOn(Run(def), "id", "CON");   // asserts exactly one 'id' error
        }

        [Fact]
        public void MakeUniqueId_ReservedBase_SuffixesInsteadOfMintingAnInvalidId()
        {
            // Coherence: the SHARED minter (editor New/Duplicate + the Story-8.4 AI-draft landing) must not hand back an
            // id its own validator now refuses on Save. RED without the fix: it returned "con".
            Assert.Equal("con_2", UnitDefinitionValidator.MakeUniqueId(new[] { "worker" }, "CON"));
            Assert.Equal("nul_2", UnitDefinitionValidator.MakeUniqueId(System.Array.Empty<string>(), "nul"));

            // And the minted id actually validates (the point of the coherence rule).
            var def = ValidUnit();
            def.Id = UnitDefinitionValidator.MakeUniqueId(System.Array.Empty<string>(), "con");
            Assert.True(Run(def).Ok);
        }

        [Fact]
        public void MakeUniqueId_NonReservedBase_IsUnchanged()
        {
            // Regression net for the added condition: the ordinary paths keep their exact prior behavior.
            Assert.Equal("grunt", UnitDefinitionValidator.MakeUniqueId(new[] { "worker", "archer" }, "grunt"));
            Assert.Equal("grunt_2", UnitDefinitionValidator.MakeUniqueId(new[] { "grunt" }, "grunt"));
            Assert.Equal("new_unit", UnitDefinitionValidator.MakeUniqueId(System.Array.Empty<string>(), "   "));
        }

        [Fact]
        public void ReservedBuildingId_IsRejected_KindedBuilding()
        {
            var def = new BuildingDefinition
            {
                Id = "nul", DisplayName = "Nul", Category = "Structure",
                Hp = 400f, ConstructionTime = 20f, SupplyBonus = 0, ProducesCategory = "None",
            };
            BuildingValidationResult r = BuildingDefinitionValidator.Validate(def);
            Assert.False(r.Ok);
            Assert.Contains("building 'nul'", r.Errors.First(e => e.FieldPath == "id").Message);
        }

        // ── DW-528: the FILENAME-level companion, for callers that decorate an id before the write ─────────────────

        [Theory]
        // The bare device, and the shapes a decorating caller actually produces.
        [InlineData("nul")]
        [InlineData("con.json")]
        [InlineData("con.json.tmp")]           // Win32 matches the segment before the FIRST dot, not "name minus ext"
        [InlineData("com1.json")]
        [InlineData("lpt9.a.b.c")]
        [InlineData("CON.JSON")]               // case-insensitive
        [InlineData("nul.x_faction.json")]     // the live faction-wizard hole: id "nul.x" + the "_faction.json" suffix
        [InlineData("con.")]                   // trailing dot stripped before the device compare
        [InlineData("con .json")]              // trailing space stripped too
        [InlineData("dir/con.json")]           // only the LAST path component is matched
        [InlineData("dir\\con.json")]          // '\' split explicitly so the verdict is the same on Linux
        public void ReservedDeviceFileName_IsRecognised(string fileName)
        {
            Assert.True(UnitDefinitionValidator.IsReservedDeviceFileName(fileName), fileName);
        }

        [Theory]
        [InlineData("con_faction.json")]       // the decoration is what makes it safe — must NOT be over-rejected
        [InlineData("con_faction.json.tmp")]
        [InlineData("console.json")]
        [InlineData("con_2.json")]
        [InlineData("nullify.json")]
        [InlineData("com0.json")]              // not a reserved device
        [InlineData("lpt0.json")]
        [InlineData("acon.json")]              // merely ENDS with a device name
        [InlineData("dir/con_faction.json")]
        [InlineData("grunt.json")]
        [InlineData(".json")]                  // empty leading segment
        [InlineData("")]
        [InlineData(null)]
        public void NonReservedDeviceFileName_IsNotFlagged(string? fileName)
        {
            Assert.False(UnitDefinitionValidator.IsReservedDeviceFileName(fileName), fileName ?? "<null>");
        }

        [Fact]
        public void ReservedDeviceFileName_AgreesWithIsReservedDeviceName_OnAnUndecoratedName()
        {
            // The two helpers are one convention: an undecorated basename must get the same verdict from both, or a
            // caller that switches between them silently changes behavior.
            foreach (object[] row in ReservedNames())
            {
                var id = (string)row[0];
                Assert.True(UnitDefinitionValidator.IsReservedDeviceName(id), id);
                Assert.True(UnitDefinitionValidator.IsReservedDeviceFileName(id), id);
            }
        }

        [Fact]
        public void OrdinaryItemAndUnit_StillValidate()
        {
            // Positive control: the two new rules add no false positives to an ordinary definition.
            Assert.True(Run(ValidUnit()).Ok);
            Assert.True(new ItemDefinitionValidator().Validate(
                new ItemDefinition { Id = "ring", Charges = 0, MaxHealthDelta = Fixed.FromInt(50) }).Ok);
        }

        // ── DW-527: hp — the FOURTH degenerate-at-zero stat, and the reconciliation of the two hp rules ─────────────
        //
        // The unit gate ran hp through the generic CheckStat ([0, 32768), INCLUSIVE of 0) while
        // BuildingDefinitionValidator rejected hp <= 0 on its own — so a 0-hp UNIT was authorable, and (because the
        // building validator reuses the unit gate on top of its own check) a negative/NaN building hp badged the same
        // control TWICE. Both halves are now one rule, owned by the shared gate; the building side keeps only its
        // HpAuthored PRESENCE check, which the shared gate cannot express.

        private static BuildingDefinition ValidBuilding() => new BuildingDefinition
        {
            Id = "barracks", DisplayName = "Barracks", Category = "Structure",
            Hp = 400f, ConstructionTime = 20f, SupplyBonus = 0, ProducesCategory = "Melee",
            AttackDamage = 0f, AttackSpeed = 0f, AttackRange = 0f, Speed = 0f, VisionRange = 0f, TrainTime = 0f,
            Supply = 0,
        };

        [Fact]
        public void ZeroHp_OnUnit_IsRejected()
        {
            // RED without the fix: CheckStat's [0, Range) admitted 0, so a unit could be saved with a zero health
            // ceiling — EntityWorld.Create seeds Health/BaseMaxHealth/EffectiveMaxHealth from it, the entity spawns
            // ALIVE at 0 HP, and ModifierStore's clamp into [0, EffectiveMaxHealth] = [0, 0] means it can never be
            // healed back above zero.
            var def = ValidUnit(); def.Hp = 0f;
            AssertSingleErrorOn(Run(def), "hp", "grunt");
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(32768f)]   // == the 16.16 ceiling (exclusive)
        public void NonFiniteOrOutOfRangeHp_IsStillRejected_ExactlyOnce(float value)
        {
            // The strictly-positive helper must keep the pre-existing finite/upper-bound teeth, not replace them — and
            // a value that fails BOTH the old and the new bound still badges the one control once.
            var def = ValidUnit(); def.Hp = value;
            AssertSingleErrorOn(Run(def), "hp", "grunt");
        }

        [Fact]
        public void SmallPositiveHp_IsValid()
        {
            // The bound is strictly positive, NOT "at least 1" — a deliberately fragile 1-HP (or sub-1-HP) unit stays
            // authorable, so this closes the degenerate case without narrowing the design space.
            var def = ValidUnit(); def.Hp = 0.25f;
            UnitValidationResult r = Run(def);
            Assert.True(r.Ok, string.Join(" | ", r.Errors.Select(e => e.Message)));
        }

        [Fact]
        public void ZeroHp_BlocksTheBalanceApplyPath_AndLeavesTargetUntouched()
        {
            // RED without the fix: "hp" is in BalanceSuggestionApplier's tunable vocabulary and TryApply gates the
            // proposal through this very validator, so a suggested hp of 0 was ONE CLICK away from being committed —
            // the same originating shape as DW-380 (which came out of the Story 8.5 review).
            var target = ValidUnit();
            var (candidate, err) = ProjectChimera.AI.BalanceSuggestionApplier.TryApply(target, "hp", 0d, null);

            Assert.Null(candidate);
            Assert.NotNull(err);
            Assert.Contains("hp", err!);
            Assert.Equal(100f, target.Hp);   // a rejected apply never mutates the original
        }

        [Fact]
        public void ZeroHp_OnBuilding_IsStillRejected_KindedBuilding()
        {
            // The building-side value rule was DELETED (the shared gate owns it now) — this proves that deletion lost
            // nothing: an authored hp of 0 is still refused, and the message still names the BUILDING, not a "unit".
            BuildingDefinition def = ValidBuilding(); def.Hp = 0f;
            BuildingValidationResult r = BuildingDefinitionValidator.Validate(def);
            Assert.False(r.Ok);
            List<(string FieldPath, string Message)> hits = r.Errors.Where(e => e.FieldPath == "hp").ToList();
            Assert.True(hits.Count == 1, $"expected exactly ONE 'hp' error, got {hits.Count}");
            Assert.Contains("building 'barracks'", hits[0].Message);
        }

        [Theory]
        [InlineData(-5f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void NonPositiveOrNonFiniteBuildingHp_ReportsExactlyOneBadge_NotTwo(float value)
        {
            // RED without the fix — and red for a reason that pre-dates DW-527's own defect: the building validator's
            // local `!IsFinite(Hp) || Hp <= 0f` branch AND the reused unit gate's generic [0, Range) CheckStat both
            // fired on a negative/NaN hp, so one control received TWO located errors. That is the doubled per-field
            // badge (D-9) — the exact reason the ledger says the two hp checks "cannot both fire without doubling the
            // badge" and had to be reconciled into one rule before hp could be tightened.
            BuildingDefinition def = ValidBuilding(); def.Hp = value;
            BuildingValidationResult r = BuildingDefinitionValidator.Validate(def);
            List<(string FieldPath, string Message)> hits = r.Errors.Where(e => e.FieldPath == "hp").ToList();
            Assert.True(hits.Count == 1,
                $"expected exactly ONE 'hp' error (a doubled badge is a bug), got {hits.Count}: " +
                string.Join(" | ", hits.Select(e => e.Message)));
        }

        [Fact]
        public void OverRangeBuildingHp_IsStillRejected_ExactlyOnce()
        {
            // The deleted local branch only ever looked at `<= 0`, so the 16.16-ceiling half of the building's hp
            // coverage always came from the reused unit gate. Pinned here so the deletion is provably a de-duplication
            // and not a narrowing: an over-range building hp is still exactly one located error.
            BuildingDefinition def = ValidBuilding(); def.Hp = 32768f;
            BuildingValidationResult r = BuildingDefinitionValidator.Validate(def);
            List<(string FieldPath, string Message)> hits = r.Errors.Where(e => e.FieldPath == "hp").ToList();
            Assert.True(hits.Count == 1, $"expected exactly ONE 'hp' error, got {hits.Count}");
            Assert.Contains("building 'barracks'", hits[0].Message);
        }

        [Fact]
        public void UnauthoredBuildingHp_StillReportsExactlyOneRequiredBadge()
        {
            // DW-55's presence rule is the one hp rule the shared gate cannot express, so it stayed on the building
            // side. Positive control that the merge guard did not swallow it AND that the shared rule (which sees the
            // inherited 100f default and passes) does not double it.
            var def = new BuildingDefinition
            {
                Id = "barracks", DisplayName = "Barracks", Category = "Structure",
                ConstructionTime = 20f, SupplyBonus = 0, ProducesCategory = "Melee",
                AttackDamage = 0f, AttackSpeed = 0f, AttackRange = 0f, Speed = 0f, VisionRange = 0f, TrainTime = 0f,
                Supply = 0,
            };
            Assert.False(def.HpAuthored);

            BuildingValidationResult r = BuildingDefinitionValidator.Validate(def);
            List<(string FieldPath, string Message)> hits = r.Errors.Where(e => e.FieldPath == "hp").ToList();
            Assert.True(hits.Count == 1, $"expected exactly ONE 'hp' error, got {hits.Count}");
            Assert.Contains("required", hits[0].Message);
        }

        [Fact]
        public void UnauthoredBuildingHp_WrittenNonPositiveThroughTheBaseReference_StillBadgesOnce()
        {
            // The merge guard's own case: BuildingDefinition's `new`-shadow setter is non-virtual, so a write through a
            // UnitDefinition-typed reference sets the value WITHOUT flagging HpAuthored. Such a def satisfies the
            // presence rule's reject AND the shared value rule's reject at once — the guard keeps the more actionable
            // "required but missing" badge and suppresses the second, so "exactly one hp error" is a property of the
            // merge rather than an accident of the 100f default.
            var def = new BuildingDefinition
            {
                Id = "barracks", DisplayName = "Barracks", Category = "Structure",
                ConstructionTime = 20f, SupplyBonus = 0, ProducesCategory = "Melee",
                AttackDamage = 0f, AttackSpeed = 0f, AttackRange = 0f, Speed = 0f, VisionRange = 0f, TrainTime = 0f,
                Supply = 0,
            };
            ((UnitDefinition)def).Hp = 0f;
            Assert.False(def.HpAuthored);
            Assert.Equal(0f, def.Hp);

            BuildingValidationResult r = BuildingDefinitionValidator.Validate(def);
            List<(string FieldPath, string Message)> hits = r.Errors.Where(e => e.FieldPath == "hp").ToList();
            Assert.True(hits.Count == 1,
                $"expected exactly ONE 'hp' error, got {hits.Count}: " + string.Join(" | ", hits.Select(e => e.Message)));
            Assert.Contains("required", hits[0].Message);
        }

        [Fact]
        public void ShippedBuildingAndUnitPostures_StayValid_UnderTheHpRule()
        {
            // Positive control against a false positive on real content: every shipped unit and building authors a
            // positive hp (alpha_faction.json / beta_faction.json range from 55 to 500), so the tightened bound rejects
            // none of it — including the zero-damage/zero-interval non-combatant posture DW-380 had to preserve.
            Assert.True(BuildingDefinitionValidator.Validate(ValidBuilding()).Ok);

            var unit = ValidUnit(); unit.Hp = 55f;   // the smallest hp any shipped unit authors
            Assert.True(Run(unit).Ok);
        }
    }
}
