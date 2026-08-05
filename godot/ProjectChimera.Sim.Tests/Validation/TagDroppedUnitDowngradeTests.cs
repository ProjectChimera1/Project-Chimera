#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// DW-652 — the FAIL-CLOSED ESCALATION walk-back (recorded decision, 2026-08-05: "revert to drop-one for the two
    /// named paths").
    ///
    /// <para><b>The defect.</b> DW-240 made an unresolvable pre-placed <c>unit_id</c> reject the WHOLE scenario. That
    /// is right for a dangling reference and wrong for two paths where the map is blameless and the cost is a
    /// fallback-map boot (or worse) instead of one missing entity:</para>
    /// <list type="number">
    ///   <item><description><b>Tag-dropped unit.</b> <see cref="UnitTagValidator.ValidateAndDropUnits"/> runs on the
    ///   loaded faction BEFORE the gate (SlotFactionResolver / ServerBootstrap / MainScene) and REMOVES any unit
    ///   carrying an unknown tag. A shipped map naming that unit then fails the gate and boots the fallback map,
    ///   where pre-DW-240 it merely lost that one unit.</description></item>
    ///   <item><description><b>The fallback mirror's worker id.</b> <c>ScenarioApplier.WorkerIdForSlot</c> fell back
    ///   to the literal id <c>"worker"</c> for ANY miss, so a threaded faction with no Worker-category unit made the
    ///   MIRROR itself name an unresolvable id — the mirror failed its own gate and the fallback boot applied NOTHING
    ///   (an empty world).</description></item>
    /// </list>
    ///
    /// <para><b>RED-teeth proof.</b> (1) Drop the <c>WasUnitDroppedForInvalidTag</c> clause in the validator's
    /// pre-placed unit gate (or the <c>NoteTagDroppedUnit</c> call in <see cref="UnitTagValidator"/>) and every
    /// <c>…IsAccepted…</c> tag-drop row turns RED. (2) Restore <c>WorkerIdForSlot</c>'s <c>?? "worker"</c> and the
    /// mirror rows turn RED. The fail-closed rows stay GREEN throughout — they pin that DW-240's actual teeth
    /// (typos, cross-faction ids, blank ids) are untouched.</para>
    /// </summary>
    public class TagDroppedUnitDowngradeTests
    {
        private static readonly ScenarioValidator Validator = new();

        // ── Fixtures ────────────────────────────────────────────────────────────────────────────────────────────

        private static UnitDefinition Unit(string id, string category = "Worker", string[]? tags = null)
            => new UnitDefinition { Id = id, DisplayName = id, Category = category, Hp = 50f, Speed = 4f, Tags = tags };

        /// <summary>A faction declaring {worker, cursed_knight}, where cursed_knight carries an UNKNOWN tag — i.e.
        /// exactly the shape <see cref="UnitTagValidator.ValidateAndDropUnits"/> compacts.</summary>
        private static FactionDefinition FactionWithABadlyTaggedUnit()
        {
            var f = new FactionDefinition { Id = "alpha", DisplayName = "Alpha" };
            f.Units.Add(Unit("worker"));
            f.Units.Add(Unit("cursed_knight", "Melee", new[] { "Undead" })); // "Undead" ∉ {Organic, Mechanical, Magical}
            return f;
        }

        private static FactionDefinition?[] SlotDefs(FactionDefinition? p1, FactionDefinition? p2 = null)
        {
            var defs = new FactionDefinition?[FactionRegistry.FACTION_ARRAY_SIZE];
            defs[(int)Faction.Player1] = p1;
            defs[(int)Faction.Player2] = p2;
            return defs;
        }

        /// <summary>A minimal two-slot model whose pre-placed units are exactly <paramref name="unitIds"/> on slot 0.</summary>
        private static ScenarioData ModelWithUnits(params string?[] unitIds)
        {
            var units = new List<ScenarioUnit>(unitIds.Length);
            for (int i = 0; i < unitIds.Length; i++)
                units.Add(new ScenarioUnit { UnitId = unitIds[i]!, Slot = 0, X = -42f + i * 3f, Z = -3f });
            return new ScenarioData
            {
                MapBounds = 120f,
                WinCondition = WinCondition.DestroyAllBuildings,
                PlayerSlots = new[]
                {
                    new ScenarioPlayerSlot { Slot = 0, StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                    new ScenarioPlayerSlot { Slot = 1, StartOre = 200f, BaseX =  45f, BaseZ = 0f },
                },
                ResourceNodes = System.Array.Empty<ScenarioResourceNode>(),
                Buildings = System.Array.Empty<ScenarioBuilding>(),
                Units = units.ToArray(),
            };
        }

        // ── (1) the tag-dropped unit: downgraded to a per-entity drop ───────────────────────────────────────────

        [Fact]
        public void PrePlacedUnitId_DroppedByTheTagValidator_IsAccepted_NotAWholeScenarioReject()
        {
            FactionDefinition alpha = FactionWithABadlyTaggedUnit();
            Assert.Single(UnitTagValidator.ValidateAndDropUnits(alpha));      // the engine removes cursed_knight...
            Assert.Null(alpha.GetUnit("cursed_knight"));                      // ...so the roster no longer resolves it

            // The map named a unit its faction really declares. Pre-fix this rejected the WHOLE scenario and booted
            // the fallback map; now it loads and only that entity is lost at apply.
            ValidationResult r = Validator.Validate(ModelWithUnits("cursed_knight"), SlotDefs(alpha));
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void PrePlacedUnitId_DroppedByTheTagValidator_LosesOnlyThatEntity_AtApply()
        {
            // The "drop-one" half of the decision, end to end: the scenario applies, the tag-dropped unit is the only
            // thing missing, and the rest of the model (the other unit, the economy) is fully applied.
            FactionDefinition alpha = FactionWithABadlyTaggedUnit();
            UnitTagValidator.ValidateAndDropUnits(alpha);
            FactionDefinition?[] defs = SlotDefs(alpha, alpha);

            ScenarioData m = ModelWithUnits("worker", "cursed_knight", "worker");
            ValidationResult r = Validator.Validate(m, defs);
            Assert.True(r.Ok, r.Error);

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), alpha, alpha);
            new ScenarioApplier(host, NullLogSink.Instance, defs).Apply(r.Value);

            int alive = 0;
            for (int i = 0; i < host.World.HighWaterMark; i++) if (host.World.IsAlive(i)) alive++;
            Assert.Equal(2, alive);                                                     // 3 authored - 1 dropped
            Assert.Equal(Fixed.FromFloat(200f).Raw, host.Resources.Ore[(int)Faction.Player1].Raw); // rest applied
        }

        [Fact]
        public void PrePlacedUnitId_NeverDeclaredByTheOwnerFaction_StillFailsClosed()
        {
            // DW-240's actual teeth, untouched: a typo names nothing the faction ever declared, so the map IS wrong
            // and the reject (not a silent short army) is the point.
            FactionDefinition alpha = FactionWithABadlyTaggedUnit();
            UnitTagValidator.ValidateAndDropUnits(alpha);
            ValidationResult r = Validator.Validate(ModelWithUnits("ghost_unit"), SlotDefs(alpha));
            Assert.False(r.Ok);
            Assert.Contains("units[0].unit_id", r.Error!);
            Assert.Contains("ghost_unit", r.Error!);
        }

        [Fact]
        public void PrePlacedUnitId_Blank_StillFailsClosed_EvenAfterATagDrop()
        {
            // A blank reference is never "known but dropped" — it is malformed, and the record must not launder it.
            FactionDefinition alpha = FactionWithABadlyTaggedUnit();
            UnitTagValidator.ValidateAndDropUnits(alpha);
            ValidationResult r = Validator.Validate(ModelWithUnits(""), SlotDefs(alpha));
            Assert.False(r.Ok);
            Assert.Contains("units[0].unit_id", r.Error!);
        }

        [Fact]
        public void TagDropRecord_IsScopedToTheFactionThatDroppedIt_NotSharedAcrossSlots()
        {
            // Slot 0's faction dropped cursed_knight; slot 1's faction never declared it. The downgrade must key on
            // the OWNER slot's roster exactly like the gate it relaxes, or one faction's drop would amnesty another's
            // dangling reference.
            FactionDefinition alpha = FactionWithABadlyTaggedUnit();
            UnitTagValidator.ValidateAndDropUnits(alpha);
            var beta = new FactionDefinition { Id = "beta", DisplayName = "Beta" };
            beta.Units.Add(Unit("forgehand"));

            ScenarioData m = ModelWithUnits("cursed_knight");
            m.Units[0].Slot = 1;   // the SAME id, but owned by beta

            ValidationResult r = Validator.Validate(m, SlotDefs(alpha, beta));
            Assert.False(r.Ok);
            Assert.Contains("beta", r.Error!);
        }

        [Fact]
        public void TagDropRecord_IsNotWrittenWhenNothingIsDropped()
        {
            var clean = new FactionDefinition { Id = "clean", DisplayName = "Clean" };
            clean.Units.Add(Unit("worker", tags: new[] { "Organic" }));
            Assert.Empty(UnitTagValidator.ValidateAndDropUnits(clean));
            Assert.Empty(clean.TagDroppedUnitIds);
            Assert.False(clean.WasUnitDroppedForInvalidTag("worker"));
        }

        [Fact]
        public void TagDropRecord_IsIdempotentAcrossRepeatedValidatePasses()
        {
            // SlotFactionResolver re-runs the drop on every apply (boot + every Edit→Play re-apply). The second pass
            // sees an already-compacted roster and must neither re-report nor duplicate the record.
            FactionDefinition alpha = FactionWithABadlyTaggedUnit();
            Assert.Single(UnitTagValidator.ValidateAndDropUnits(alpha));
            Assert.Empty(UnitTagValidator.ValidateAndDropUnits(alpha));
            Assert.Single(alpha.TagDroppedUnitIds);
            Assert.True(alpha.WasUnitDroppedForInvalidTag("cursed_knight"));
        }

        [Fact]
        public void TagDropRecord_IsNotSerializedBackIntoFactionJson()
        {
            // [JsonIgnore] — the Faction Definer wizard re-serializes a loaded FactionDefinition, and a runtime-only
            // drop record must never leak into authored content.
            FactionDefinition alpha = FactionWithABadlyTaggedUnit();
            UnitTagValidator.ValidateAndDropUnits(alpha);
            string json = System.Text.Json.JsonSerializer.Serialize(alpha, FactionDefinition.JsonOptions);
            Assert.DoesNotContain("TagDroppedUnitIds", json);
            Assert.DoesNotContain("tag_dropped", json);
            Assert.DoesNotContain("cursed_knight", json);   // the unit itself was compacted out of Units too
        }

        // ── (2) the fallback mirror's worker id ─────────────────────────────────────────────────────────────────

        [Fact]
        public void FallbackMirror_ForAFactionWithNoWorkerCategory_NamesAResolvableId_AndValidates()
        {
            // The degenerate fallback DW-652 names: a threaded faction declaring units but no Worker-category one.
            // Pre-fix the mirror named the literal "worker", failed its own gate, and the fallback boot applied
            // NOTHING (empty world). Now it names a unit the roster actually declares.
            var noWorker = new FactionDefinition { Id = "no_worker", DisplayName = "No Worker" };
            noWorker.Units.Add(Unit("rune_caster", "Ranged"));
            FactionDefinition?[] defs = SlotDefs(noWorker, noWorker);

            ScenarioData mirror = ScenarioApplier.BuildFallbackMirror(defs);
            Assert.Equal(4, mirror.Units.Length);
            foreach (ScenarioUnit u in mirror.Units) Assert.Equal("rune_caster", u.UnitId);

            ValidationResult r = Validator.Validate(mirror, defs);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void FallbackMirror_ForAFactionWhoseWorkerIsUncategorized_KeepsTheConventionalId()
        {
            // The roster declares "worker" but under some other category — the conventional literal still resolves,
            // so it is preferred over an arbitrary first-unit pick (closest to the legacy behavior).
            var odd = new FactionDefinition { Id = "odd", DisplayName = "Odd" };
            odd.Units.Add(Unit("scout", "Ranged"));
            odd.Units.Add(Unit("worker", "Builder"));
            FactionDefinition?[] defs = SlotDefs(odd);

            ScenarioData mirror = ScenarioApplier.BuildFallbackMirror(defs);
            Assert.Equal("worker", mirror.Units[0].UnitId);
            Assert.True(Validator.Validate(mirror, defs).Ok);
        }

        [Fact]
        public void FallbackMirror_ForAFactionWithNoUsableUnitAtAll_OmitsThatSlotsRows_AndStillValidates()
        {
            // The worst case: a faction whose whole roster was tag-dropped. There is no id to name, so the mirror
            // omits that slot's two worker rows instead of dangling a reference — the fallback board still boots
            // (bases, resources, command centres) rather than collapsing to an empty world.
            var emptied = FactionWithABadlyTaggedUnit();
            emptied.Units.Clear();
            var beta = new FactionDefinition { Id = "beta", DisplayName = "Beta" };
            beta.Units.Add(Unit("forgehand"));
            FactionDefinition?[] defs = SlotDefs(emptied, beta);

            ScenarioData mirror = ScenarioApplier.BuildFallbackMirror(defs);
            Assert.Equal(2, mirror.Units.Length);                 // slot 0 omitted, slot 1 kept
            foreach (ScenarioUnit u in mirror.Units)
            {
                Assert.Equal(1, u.Slot);
                Assert.Equal("forgehand", u.UnitId);
            }
            Assert.NotEmpty(mirror.Buildings);                    // the board itself is intact
            Assert.NotEmpty(mirror.ResourceNodes);

            ValidationResult r = Validator.Validate(mirror, defs);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void FallbackMirror_WithNoDefsThreaded_IsUnchanged_FourConventionalWorkerRows()
        {
            // The baseline every parity/golden pin depends on: no defs ⇒ four "worker" rows in the same order at the
            // same coordinates. The hardening must be invisible here.
            ScenarioData mirror = ScenarioApplier.BuildFallbackMirror();
            Assert.Equal(4, mirror.Units.Length);
            Assert.Equal((-42f, -3f, 0), (mirror.Units[0].X, mirror.Units[0].Z, mirror.Units[0].Slot));
            Assert.Equal((-42f,  3f, 0), (mirror.Units[1].X, mirror.Units[1].Z, mirror.Units[1].Slot));
            Assert.Equal(( 42f, -3f, 1), (mirror.Units[2].X, mirror.Units[2].Z, mirror.Units[2].Slot));
            Assert.Equal(( 42f,  3f, 1), (mirror.Units[3].X, mirror.Units[3].Z, mirror.Units[3].Slot));
            foreach (ScenarioUnit u in mirror.Units) Assert.Equal("worker", u.UnitId);
            Assert.True(Validator.Validate(mirror).Ok);
        }

        [Fact]
        public void FallbackMirror_ForAFactionWhoseWorkerWasTagDropped_StillValidates()
        {
            // Both halves of the decision meeting on one path: the faction's Worker-category unit is the one the tag
            // validator removed. WorkerIdForSlot must not name it (it no longer resolves), and whatever it names must
            // clear the gate — otherwise the fallback boot is the empty world again.
            var f = new FactionDefinition { Id = "alpha", DisplayName = "Alpha" };
            f.Units.Add(Unit("worker", "Worker", new[] { "Undead" }));   // dropped
            f.Units.Add(Unit("rune_caster", "Ranged"));                  // survives
            Assert.Single(UnitTagValidator.ValidateAndDropUnits(f));
            FactionDefinition?[] defs = SlotDefs(f, f);

            ScenarioData mirror = ScenarioApplier.BuildFallbackMirror(defs);
            foreach (ScenarioUnit u in mirror.Units) Assert.Equal("rune_caster", u.UnitId);
            Assert.True(Validator.Validate(mirror, defs).Ok);
        }
    }
}
