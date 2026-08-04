#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;              // EntityWorld, Fixed, Faction
using ProjectChimera.Core.Definitions;  // AbilityDefinition, AbilityValidator, AbilityValidationResult, AbilityRegistry
using ProjectChimera.Core.Sim;          // ILogSink — the AR-4 injected diagnostic seam
using ProjectChimera.Effects;           // AbilityCastSystem, HealEffect
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Epic-15 bundle <c>ability-cast-path-hardening</c> — DW-283 / DW-284 / DW-285.
    ///
    /// <list type="bullet">
    /// <item><b>DW-283</b> — the cast pipeline's affordability pre-checks are <c>have &gt;= cost</c> comparisons, which
    ///   a NEGATIVE authored cost passes trivially, while the debits disagree about it: <c>TryDebitEnergy</c> REFUSES a
    ///   negative cost (no mutation) but <c>SpendOre</c>/<c>SpendCrystal</c> subtract it, i.e. GRANT resources. The
    ///   debit return values were DISCARDED, so the graph ran and the cooldown started on a partially-spent cast.</item>
    /// <item><b>DW-284</b> — <c>SecondsToTicks</c> multiplied through <c>Fixed.operator*</c>, whose 16.16 product is
    ///   cast back to <c>int</c>; above ≈1092.27 s that WRAPPED to a NEGATIVE tick count, which the <c>&gt; 0</c>
    ///   countdown gate reads as "ready" — the longest cooldown an author could write became NO cooldown. The
    ///   validator had only a lower bound.</item>
    /// <item><b>DW-285</b> — every ability-resolution failure degraded SILENTLY: link-time drops (unknown id, over-cap
    ///   active, shadowed second passive) and the runtime bare-returns (bad slot, out-of-registry index, broken
    ///   aura/self-passive index). A mis-wired registry was indistinguishable from "the player didn't cast".</item>
    /// </list>
    ///
    /// Every test here is RED without its fix. Godot-free, Fixed-only, hermetic (no filesystem).
    /// </summary>
    public class AbilityCastPathHardeningTests
    {
        private const int P1 = (int)Faction.Player1;

        /// <summary>Capturing <see cref="ILogSink"/> (the NullLogSink pattern, but recording) — the DW-304 precedent.</summary>
        private sealed class RecordingSink : ILogSink
        {
            public readonly List<string> Infos = new();
            public readonly List<string> Warns = new();
            public void Info(string message) => Infos.Add(message);
            public void Warn(string message) => Warns.Add(message);
        }

        // ─────────────────────────────── DW-283: negative-cost partial spend ────────────────────────────────

        /// <summary>
        /// A hand-built (validator-bypassing) ability with a NEGATIVE ore cost. Pre-fix: <c>CanAffordOre(ore &gt;= -50)</c>
        /// passes, <c>SpendOre</c> subtracts a negative and CREDITS 50 ore, the heal runs and the cooldown starts — a
        /// resource-printing exploit riding a "refused" energy debit. Post-fix the whole cast is refused atomically.
        /// </summary>
        [Fact]
        public void NegativeOreCost_IsRefusedAtomically_AndNeverGrantsOre()
        {
            var h = new CastHarness(NegativeCostHeal(costEnergy: 0, costOre: -50, costCrystal: 0));
            int c = h.Caster("neg_cost_heal", energy: 100);
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(10));
            h.World.Health[c] = Fixed.FromInt(50);

            h.IssueAndTick(c, -1);

            Assert.Equal(Fixed.FromInt(10).Raw, h.Resources.Ore[P1].Raw);  // NOT credited by the negative "cost"
            Assert.Equal(Fixed.FromInt(50).Raw, h.World.Health[c].Raw);    // the effect graph never ran
            Assert.Equal(0, h.Cooldown(c));                                // no cooldown started
            Assert.Equal(Fixed.FromInt(100).Raw, h.World.Energy[c].Raw);   // energy untouched
        }

        /// <summary>
        /// A NEGATIVE crystal cost — the same grant defect on the second resource, proving the gate covers every cost
        /// axis rather than the one the first test happened to pick.
        /// </summary>
        [Fact]
        public void NegativeCrystalCost_IsRefusedAtomically_AndNeverGrantsCrystal()
        {
            var h = new CastHarness(NegativeCostHeal(costEnergy: 0, costOre: 0, costCrystal: -25));
            int c = h.Caster("neg_cost_heal", energy: 100);
            h.Resources.AddCrystal(Faction.Player1, Fixed.FromInt(5));
            h.World.Health[c] = Fixed.FromInt(50);

            h.IssueAndTick(c, -1);

            Assert.Equal(Fixed.FromInt(5).Raw, h.Resources.Crystal[P1].Raw);
            Assert.Equal(Fixed.FromInt(50).Raw, h.World.Health[c].Raw);
            Assert.Equal(0, h.Cooldown(c));
        }

        /// <summary>
        /// A NEGATIVE energy cost is the asymmetric half: <c>TryDebitEnergy</c> refuses it (returns false, mutates
        /// nothing) while every other cost is spent and the graph runs — the exact "check passed, debit didn't" split
        /// DW-283 names. Pre-fix the ore WAS spent and the unit WAS healed on a cast whose energy price never applied.
        /// </summary>
        [Fact]
        public void NegativeEnergyCost_IsRefusedAtomically_WithoutSpendingTheOtherCosts()
        {
            var h = new CastHarness(NegativeCostHeal(costEnergy: -10, costOre: 5, costCrystal: 0));
            int c = h.Caster("neg_cost_heal", energy: 100);
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(50));
            h.World.Health[c] = Fixed.FromInt(50);

            h.IssueAndTick(c, -1);

            Assert.Equal(Fixed.FromInt(50).Raw, h.Resources.Ore[P1].Raw); // ore NOT spent on a malformed cast
            Assert.Equal(Fixed.FromInt(100).Raw, h.World.Energy[c].Raw);
            Assert.Equal(Fixed.FromInt(50).Raw, h.World.Health[c].Raw);   // the effect graph never ran
            Assert.Equal(0, h.Cooldown(c));
        }

        /// <summary>The refusal is observable, not silent: it warns through the sink and pushes an OrderDenied cue.</summary>
        [Fact]
        public void NegativeCost_RefusalIsDiagnosed()
        {
            var sink = new RecordingSink();
            var h = new CastHarness(sink, NegativeCostHeal(costEnergy: 0, costOre: -50, costCrystal: 0));
            int c = h.Caster("neg_cost_heal", energy: 100);

            h.IssueAndTick(c, -1);

            Assert.Single(sink.Warns);
            Assert.Contains("NEGATIVE cost", sink.Warns[0]);
            Assert.Contains("neg_cost_heal", sink.Warns[0]);
        }

        /// <summary>A well-formed cast is COMPLETELY unaffected by the new gate (no behavior/checksum drift).</summary>
        [Fact]
        public void NonNegativeCosts_StillDebitAndRun()
        {
            var h = new CastHarness(AbilityTestAbilities.SelfHeal(costEnergy: 20, costOre: 5, costCrystal: 2, cooldownSec: 3, heal: 40));
            int c = h.Caster("test_heal", energy: 100);
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(50));
            h.Resources.AddCrystal(Faction.Player1, Fixed.FromInt(50));
            h.World.Health[c] = Fixed.FromInt(50);

            h.IssueAndTick(c, -1);

            Assert.Equal(Fixed.FromInt(80).Raw, h.World.Energy[c].Raw);
            Assert.Equal(Fixed.FromInt(45).Raw, h.Resources.Ore[P1].Raw);
            Assert.Equal(Fixed.FromInt(48).Raw, h.Resources.Crystal[P1].Raw);
            Assert.Equal(Fixed.FromInt(90).Raw, h.World.Health[c].Raw);
            Assert.Equal(90, h.Cooldown(c));
        }

        /// <summary>A Self heal whose costs are set VERBATIM (including negatives) — deliberately not validator-clean.</summary>
        private static AbilityDefinition NegativeCostHeal(int costEnergy, int costOre, int costCrystal) =>
            new AbilityDefinition
            {
                Id = "neg_cost_heal", DisplayName = "Negative Cost Heal", Targeting = "Self",
                CostEnergy = Fixed.FromInt(costEnergy), CostOre = costOre, CostCrystal = costCrystal,
                Cooldown = Fixed.FromInt(3), EffectGraph = new HealEffect(Fixed.FromInt(40)),
            };

        // ────────────────────────── DW-284: 16.16 cooldown-conversion overflow ──────────────────────────

        /// <summary>
        /// The headline regression: a 2000 s cooldown. Pre-fix the 16.16 product wrapped and
        /// <c>SecondsToTicks</c> returned −5536 — a NEGATIVE remaining-tick count, which the countdown gate
        /// (<c>&gt; 0</c>) reads as "ready", so the ability was instantly re-castable forever. Post-fix: 2000 × 30.
        /// </summary>
        [Fact]
        public void SecondsToTicks_AboveTheOverflowBound_StaysPositiveAndExact()
        {
            Assert.Equal(60000, AbilityCastSystem.SecondsToTicks(Fixed.FromInt(2000)));
            Assert.Equal(90000, AbilityCastSystem.SecondsToTicks(Fixed.FromInt(3000)));
            // The extreme: the largest value the 16.16 type can hold at all (32767.99998 s) still converts positive.
            Assert.True(AbilityCastSystem.SecondsToTicks(Fixed.MaxValue) > 0);
        }

        /// <summary>
        /// Byte-identity below/at the bound — the determinism guarantee that keeps every folded
        /// <c>AbilityCooldownTicks</c> value (and therefore every golden) exactly where it was. The shipped cooldowns
        /// are 0/3/6/10/12/15/20 s; the boundary value is the last one the OLD arithmetic also got right.
        /// </summary>
        [Fact]
        public void SecondsToTicks_AtAndBelowTheBound_IsUnchanged()
        {
            Assert.Equal(0,   AbilityCastSystem.SecondsToTicks(Fixed.Zero));
            Assert.Equal(90,  AbilityCastSystem.SecondsToTicks(Fixed.FromInt(3)));
            Assert.Equal(180, AbilityCastSystem.SecondsToTicks(Fixed.FromInt(6)));
            Assert.Equal(300, AbilityCastSystem.SecondsToTicks(Fixed.FromInt(10)));
            Assert.Equal(360, AbilityCastSystem.SecondsToTicks(Fixed.FromInt(12)));
            Assert.Equal(450, AbilityCastSystem.SecondsToTicks(Fixed.FromInt(15)));
            Assert.Equal(600, AbilityCastSystem.SecondsToTicks(Fixed.FromInt(20)));
            // Sub-tick fraction still truncates toward zero exactly as before (0.01 s → 0 ticks at 30 tps).
            Assert.Equal(0, AbilityCastSystem.SecondsToTicks(Fixed.FromRaw(655)));
            // The bound itself: raw int.MaxValue/30 → 32767 ticks, the last value the old int32 product also produced.
            Assert.Equal(32767, AbilityCastSystem.SecondsToTicks(AbilityCastSystem.MaxCooldownSeconds));
        }

        /// <summary>
        /// The bound is exactly the pre-fix overflow threshold — derived here independently of the production
        /// constant, so a silent redefinition of <c>MaxCooldownSeconds</c> fails this test rather than sliding through.
        /// </summary>
        [Fact]
        public void MaxCooldownSeconds_IsTheExact1616OverflowThreshold()
        {
            Assert.Equal(int.MaxValue / SimulationLoop.TICKS_PER_SECOND, AbilityCastSystem.MaxCooldownSeconds.Raw);
            // One raw tick past the bound is precisely where the OLD int32 16.16 product left the int range.
            Assert.True((long)(AbilityCastSystem.MaxCooldownSeconds.Raw + 1) * SimulationLoop.TICKS_PER_SECOND > int.MaxValue);
            Assert.True((long)AbilityCastSystem.MaxCooldownSeconds.Raw * SimulationLoop.TICKS_PER_SECOND <= int.MaxValue);
        }

        /// <summary>The validator's NEW upper bound rejects an unrepresentable cooldown (it had only a lower bound).</summary>
        [Fact]
        public void Validator_RejectsCooldownAboveTheConversionBound()
        {
            AbilityValidationResult r = new AbilityValidator().Validate(WithCooldown(Fixed.FromInt(2000)));
            Assert.False(r.Ok);
            Assert.Contains("cooldown", r.Error!);
            Assert.Contains("long_cd", r.Error!);
        }

        /// <summary>...and accepts the boundary value itself (the gate is an upper BOUND, not an off-by-one wall).</summary>
        [Fact]
        public void Validator_AcceptsCooldownAtTheConversionBound()
        {
            Assert.True(new AbilityValidator().Validate(WithCooldown(AbilityCastSystem.MaxCooldownSeconds)).Ok);
            Assert.False(new AbilityValidator().Validate(
                WithCooldown(Fixed.FromRaw(AbilityCastSystem.MaxCooldownSeconds.Raw + 1))).Ok);
        }

        /// <summary>Shipped-shaped cooldowns still validate (the bound cannot have reject-shadowed real content).</summary>
        [Fact]
        public void Validator_StillAcceptsShippedCooldowns()
        {
            foreach (int sec in new[] { 0, 3, 6, 10, 12, 15, 20 })
                Assert.True(new AbilityValidator().Validate(WithCooldown(Fixed.FromInt(sec))).Ok, $"cooldown {sec}s");
        }

        private static AbilityDefinition WithCooldown(Fixed cooldown) => new AbilityDefinition
        {
            Id = "long_cd", DisplayName = "Long Cooldown", Targeting = "Self",
            Cooldown = cooldown, EffectGraph = new HealEffect(Fixed.FromInt(1)),
        };

        // ─────────────────── DW-285: link-time + runtime resolution failures are diagnosed ───────────────────

        /// <summary>An id the registry does not hold is still DROPPED (fail-open), but no longer silently.</summary>
        [Fact]
        public void ResolveAbilities_UnknownId_IsDroppedAndWarned()
        {
            var registry = new AbilityRegistry(new[] { AbilityTestAbilities.MinorHeal() });
            var def = new UnitDefinition { Id = "grunt", Abilities = new[] { "minor_heal", "no_such_ability" } };
            var sink = new RecordingSink();

            def.ResolveAbilities(registry, sink);

            Assert.Single(def.AbilityIndices);                // the known one survived
            Assert.Single(sink.Warns);
            Assert.Contains("no_such_ability", sink.Warns[0]);
            Assert.Contains("grunt", sink.Warns[0]);
        }

        /// <summary>An active ability past <c>MAX_ABILITIES_PER_UNIT</c> is still capped away — now with a reason.</summary>
        [Fact]
        public void ResolveAbilities_OverCapActive_IsDroppedAndWarned()
        {
            int max = EntityWorld.MAX_ABILITIES_PER_UNIT;
            var defs = new List<AbilityDefinition>();
            var ids  = new List<string>();
            for (int i = 0; i <= max; i++)          // one MORE than the cap
            {
                string id = $"active_{i}";
                defs.Add(new AbilityDefinition
                {
                    Id = id, DisplayName = id, Targeting = "Self", EffectGraph = new HealEffect(Fixed.FromInt(1)),
                });
                ids.Add(id);
            }
            var sink = new RecordingSink();
            var unit = new UnitDefinition { Id = "overloaded", Abilities = ids.ToArray() };

            unit.ResolveAbilities(new AbilityRegistry(defs), sink);

            Assert.Equal(max, unit.AbilityIndices.Length);
            Assert.Single(sink.Warns);
            Assert.Contains("MAX_ABILITIES_PER_UNIT", sink.Warns[0]);
            Assert.Contains($"active_{max}", sink.Warns[0]); // the LAST declared one is the dropped one
        }

        /// <summary>A second passive of the same kind loses to the first — the same silent drop, now reported.</summary>
        [Fact]
        public void ResolveAbilities_ShadowedSecondPassive_IsDroppedAndWarned()
        {
            var second = PassiveTestAbilities.AuraGuard();
            second.Id = "aura_guard_b";                                  // a distinct id, same 'aura' activation
            var registry = new AbilityRegistry(new[] { PassiveTestAbilities.AuraGuard(), second });
            var unit = new UnitDefinition { Id = "double_aura", Abilities = new[] { "aura_guard", "aura_guard_b" } };
            var sink = new RecordingSink();

            unit.ResolveAbilities(registry, sink);

            Assert.True(unit.AuraAbilityIndex >= 0);
            Assert.Single(sink.Warns);
            Assert.Contains("aura_guard_b", sink.Warns[0]);
            Assert.Contains("aura", sink.Warns[0]);
        }

        /// <summary>A clean roster produces NO diagnostics (the channel must not cry wolf on valid content).</summary>
        [Fact]
        public void ResolveAbilities_CleanRoster_WarnsNothing()
        {
            var registry = new AbilityRegistry(new[] { AbilityTestAbilities.MinorHeal(), PassiveTestAbilities.AuraGuard() });
            var unit = new UnitDefinition { Id = "clean", Abilities = new[] { "minor_heal", "aura_guard" } };
            var sink = new RecordingSink();

            unit.ResolveAbilities(registry, sink);

            Assert.Empty(sink.Warns);
            Assert.Single(unit.AbilityIndices);
            Assert.True(unit.AuraAbilityIndex >= 0);
        }

        /// <summary>The no-sink overload still resolves identically — the diagnostics are additive, never behavioral.</summary>
        [Fact]
        public void ResolveAbilities_WithAndWithoutSink_ResolveIdentically()
        {
            var registry = new AbilityRegistry(new[] { AbilityTestAbilities.MinorHeal() });
            var quiet = new UnitDefinition { Id = "g", Abilities = new[] { "minor_heal", "ghost" } };
            var loud  = new UnitDefinition { Id = "g", Abilities = new[] { "minor_heal", "ghost" } };

            quiet.ResolveAbilities(registry);
            loud.ResolveAbilities(registry, new RecordingSink());

            Assert.Equal(quiet.AbilityIndices, loud.AbilityIndices);
            Assert.Equal(quiet.AuraAbilityIndex, loud.AuraAbilityIndex);
            Assert.Equal(quiet.SelfPassiveAbilityIndex, loud.SelfPassiveAbilityIndex);
        }

        /// <summary>
        /// Runtime half: a slot index past the unit's ability count (an AbilityCount that shrank between the order and
        /// the tick) was a bare <c>return</c>. It stays a no-op — but now warns and pushes a denial cue.
        /// </summary>
        [Fact]
        public void TryCast_SlotBeyondAbilityCount_WarnsInsteadOfVanishing()
        {
            var sink = new RecordingSink();
            var h = new CastHarness(sink, AbilityTestAbilities.MinorHeal());
            int c = h.Caster("minor_heal", energy: 100);
            h.World.Health[c] = Fixed.FromInt(50);

            h.PendSlotAndTick(c, slot: 2, targetId: -1);   // the caster holds exactly 1 slot

            Assert.Single(sink.Warns);
            Assert.Contains("slot 2", sink.Warns[0]);
            Assert.Equal(Fixed.FromInt(50).Raw, h.World.Health[c].Raw); // still the same deterministic no-op
            Assert.Equal(Fixed.FromInt(100).Raw, h.World.Energy[c].Raw);
        }

        /// <summary>
        /// Runtime half: an <c>AbilityId</c> outside the registry (a def resolved against a different registry, or an
        /// unresolved back-fill) was a bare <c>return</c> — the classic "the button does nothing" wiring bug.
        /// </summary>
        [Fact]
        public void TryCast_AbilityIdOutsideRegistry_WarnsInsteadOfVanishing()
        {
            var sink = new RecordingSink();
            var h = new CastHarness(sink, AbilityTestAbilities.MinorHeal());
            int c = h.Caster("minor_heal", energy: 100);
            h.World.AbilityId[c * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = 999; // past the 1-ability registry
            h.World.Health[c] = Fixed.FromInt(50);

            h.IssueAndTick(c, -1);

            Assert.Single(sink.Warns);
            Assert.Contains("999", sink.Warns[0]);
            Assert.Equal(Fixed.FromInt(50).Raw, h.World.Health[c].Raw);
        }

        /// <summary>A healthy cast produces NO warning — the runtime channel stays quiet on the happy path.</summary>
        [Fact]
        public void TryCast_HealthyCast_WarnsNothing()
        {
            var sink = new RecordingSink();
            var h = new CastHarness(sink, AbilityTestAbilities.MinorHeal());
            int c = h.Caster("minor_heal", energy: 100);
            h.World.Health[c] = Fixed.FromInt(50);

            h.IssueAndTick(c, -1);

            Assert.Empty(sink.Warns);
            Assert.Equal(Fixed.FromInt(90).Raw, h.World.Health[c].Raw);
        }

        /// <summary>A broken self-passive index silently skipped the install for the unit's whole life; now it warns.</summary>
        [Fact]
        public void InstallSelfPassive_IndexOutsideRegistry_Warns()
        {
            var sink = new RecordingSink();
            var h = new CastHarness(sink, AbilityTestAbilities.MinorHeal());
            int id = h.World.Create(default, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            h.World.SelfPassiveAbilityIndex[id] = 42;

            h.Cast.InstallSelfPassive(h.World, id);

            Assert.Single(sink.Warns);
            Assert.Contains("SelfPassiveAbilityIndex=42", sink.Warns[0]);
        }

        /// <summary>−1 (the normal "no self-passive" value) must NOT be reported — that is every ordinary unit.</summary>
        [Fact]
        public void InstallSelfPassive_NoPassive_WarnsNothing()
        {
            var sink = new RecordingSink();
            var h = new CastHarness(sink, AbilityTestAbilities.MinorHeal());
            int id = h.World.Create(default, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            h.Cast.InstallSelfPassive(h.World, id);

            Assert.Empty(sink.Warns);
        }

        /// <summary>
        /// The aura driver is a PER-TICK loop, so its diagnostic is de-duplicated per owner: a broken index reports
        /// once, not 30×/second forever. (Un-deduped this would be 10 warnings after 10 ticks.)
        /// </summary>
        [Fact]
        public void TickAuras_BrokenIndex_WarnsOncePerOwnerNotPerTick()
        {
            var sink = new RecordingSink();
            var h = new CastHarness(sink, AbilityTestAbilities.MinorHeal());
            int id = h.World.Create(default, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            h.World.AuraAbilityIndex[id] = 77;

            h.TickCast(10);

            Assert.Single(sink.Warns);
            Assert.Contains("AuraAbilityIndex=77", sink.Warns[0]);
        }

        /// <summary>−1 (no aura — the overwhelming majority of units) is never reported, on any number of ticks.</summary>
        [Fact]
        public void TickAuras_NoAura_WarnsNothing()
        {
            var sink = new RecordingSink();
            var h = new CastHarness(sink, AbilityTestAbilities.MinorHeal());
            h.World.Create(default, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            h.TickCast(10);

            Assert.Empty(sink.Warns);
        }

        /// <summary>
        /// Determinism guard for the whole DW-285 seam: attaching a sink must change NOTHING the simulation can read.
        /// Two identically-seeded harnesses — one with a sink, one without — driven through the same broken-wiring
        /// cast schedule must end on identical folded state (energy, health, cooldown ticks).
        /// </summary>
        [Fact]
        public void DiagnosticSink_DoesNotPerturbTheSimulation()
        {
            static (Fixed energy, Fixed health, int cd) Run(ILogSink? log)
            {
                var h = new CastHarness(log, AbilityTestAbilities.MinorHeal());
                int c = h.Caster("minor_heal", energy: 100);
                h.World.Health[c] = Fixed.FromInt(50);
                h.World.AuraAbilityIndex[c] = 77;                  // broken aura → the per-tick diagnostic site
                h.PendSlotAndTick(c, slot: 3, targetId: -1);       // broken slot  → the cast diagnostic site
                h.IssueAndTick(c, -1);                             // a real, healthy cast
                h.TickCast(5);
                return (h.World.Energy[c], h.World.Health[c], h.Cooldown(c));
            }

            var quiet = Run(null);
            var loud  = Run(new RecordingSink());

            Assert.Equal(quiet.energy.Raw, loud.energy.Raw);
            Assert.Equal(quiet.health.Raw, loud.health.Raw);
            Assert.Equal(quiet.cd, loud.cd);
        }
    }
}
