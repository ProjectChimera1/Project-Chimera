#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectChimera.Combat;             // ArmorType
using ProjectChimera.Core;               // EntityWorld, Fixed, FixedVec3, Faction, FactionRegistry
using ProjectChimera.Core.Definitions;   // AbilityRegistry, AbilityDefinition, AbilityValidator, FactionDefinition, UnitDefinition
using ProjectChimera.Core.Sim;           // SimulationHost, NullLogSink
using ProjectChimera.Multiplayer;        // OrderApplier, UnitOrder, UnitCommand
using ProjectChimera.Sim.Tests.Golden;   // GoldenChecksumReplay, GoldenHarness (the two-run determinism shape)
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 5.4 (FR-20) — real-content integration teeth for the two signature mechanics Story 2.10/2.13 already
    /// attached to the shipped rosters (Sanguine Furnace's <c>furnace_trickle</c>/<c>furnace_pour</c> on beta's
    /// units, Equal Exchange's <c>spike_transmutation</c> on alpha's <c>infantry</c>) but that NOTHING previously
    /// drove through a running sim with the REAL <c>FactionDefinition</c> JSON + REAL <see cref="AbilityRegistry"/>.
    /// Every test here loads the shipped <c>alpha_faction.json</c>/<c>beta_faction.json</c> and the shipped
    /// <c>resources/data/abilities/*.json</c> from disk — never a hand-built fixture standing in for shipped
    /// content — and drives them through a real <see cref="SimulationHost"/> (the same 15-system tick order the
    /// live game runs). This story fixes ONLY alpha's dangling <c>signature_mechanic_effect_id</c> descriptor
    /// string (now <c>spike_transmutation</c>, the actually-attached ability) and adds this coverage; the mechanics
    /// themselves (<c>AbilityCastSystem</c>'s <c>cost_health</c> gate, <c>Persistent</c>+<c>HealEffect</c>,
    /// <c>ModifierStore</c>) are 2.10/2.13-owned and untouched here.
    /// </summary>
    public class SignatureMechanicRealContentTests
    {
        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));

        // ── Real-content resolution (mirrors FactionValidatorTests.ResolveDataPath / CanonicalScenarioTests.DataFile) ──

        private static string ResolveDataDir(string sub)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", sub);
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/{sub} above {AppContext.BaseDirectory}");
        }

        /// <summary>
        /// Shipped-roster floor for the REAL ability registry: 10 files lived under <c>resources/data/abilities/</c>
        /// when this guard was written (DW-107). <see cref="AbilityRegistry.LoadFromDirectory"/> SILENTLY excludes
        /// any file that fails <see cref="AbilityValidator"/>, and no callback can ever fire for a file that was
        /// deleted outright — so without a count floor a bad roster edit shrinks the registry and every
        /// "real content" test in this class keeps passing against less-than-shipped content. Revise this floor
        /// only on a DELIBERATE shipped-roster change.
        /// </summary>
        private const int MIN_SHIPPED_ABILITY_COUNT = 10;

        /// <summary>
        /// Loads the REAL shipped alpha/beta <see cref="FactionDefinition"/>s and the REAL
        /// <see cref="AbilityRegistry"/> (every validated ability under <c>resources/data/abilities/</c>), then
        /// resolves every roster unit's abilities against it — the exact scenario-link sequence
        /// <c>MainScene</c>/<c>ServerBootstrap</c> run (Story 2.4b), never skipped here.
        /// DW-107: fails LOUD on both silent-shrink paths — a shipped file failing validation (via
        /// <c>onSkipped</c>, previously left at its default <c>null</c>) and a shipped file disappearing
        /// entirely (via the <see cref="MIN_SHIPPED_ABILITY_COUNT"/> floor, which a skip callback can never see).
        /// </summary>
        private static (FactionDefinition alpha, FactionDefinition beta, AbilityRegistry registry) LoadRealContent()
        {
            var skippedAbilityFiles = new List<string>();
            AbilityRegistry registry = AbilityRegistry.LoadFromDirectory(ResolveDataDir("abilities"), skippedAbilityFiles.Add);
            Assert.True(skippedAbilityFiles.Count == 0,
                $"shipped ability file(s) failed validation and were silently excluded from the registry: {string.Join(", ", skippedAbilityFiles)}");
            Assert.True(registry.Count >= MIN_SHIPPED_ABILITY_COUNT,
                $"real ability registry holds {registry.Count} abilities, below the shipped floor of {MIN_SHIPPED_ABILITY_COUNT} — shipped content shrank (deleted/moved file?); revise MIN_SHIPPED_ABILITY_COUNT only on a deliberate roster change.");
            FactionDefinition alpha = FactionDefinition.LoadFromFile(Path.Combine(ResolveDataDir("factions"), "alpha_faction.json"));
            FactionDefinition beta  = FactionDefinition.LoadFromFile(Path.Combine(ResolveDataDir("factions"), "beta_faction.json"));
            foreach (UnitDefinition u in alpha.Units) u.ResolveAbilities(registry);
            foreach (UnitDefinition u in beta.Units)  u.ResolveAbilities(registry);
            return (alpha, beta, registry);
        }

        /// <summary>
        /// DW-108: the pre-damage wound is DERIVED from the unit's real shipped <c>Hp</c> (one quarter of it),
        /// never a fixed magic magnitude — so a future roster edit lowering a unit's Hp can never drive it to or
        /// below zero before the regen loop runs, which would silently void the isolation these tests exist to
        /// prove (the old fixed <c>-30</c> was safe only while every wounded unit stayed above 30 Hp). Uses the
        /// same <see cref="Fixed.FromFloat"/> conversion the spawn calls above feed <c>world.Create</c>.
        /// </summary>
        private static Fixed QuarterHpWound(UnitDefinition def)
        {
            Fixed wound = Fixed.FromFloat(def.Hp) / Fixed.FromInt(4);
            Assert.True(wound > Fixed.Zero,
                $"derived pre-damage for '{def.Id}' (Hp={def.Hp}) is not positive — it cannot wound the unit below max, so a regen assertion against it would be vacuous.");
            return wound;
        }

        /// <summary>A fully-wired real-content host (ChecksumInterval=1, parity with the goldens).</summary>
        private static SimulationHost NewHost(FactionDefinition alpha, FactionDefinition beta, AbilityRegistry registry)
        {
            SimulationHost host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), alpha, beta, registry: registry);
            host.ChecksumInterval = 1;
            return host;
        }

        // ── AC1 — Sanguine Furnace real-content regen isolation ──────────────────────────────────────────────

        [Fact]
        public void SanguineFurnace_RealBetaUnit_Regenerates_RealAlphaUnit_DoesNot()
        {
            var (alpha, beta, registry) = LoadRealContent();
            SimulationHost host = NewHost(alpha, beta, registry);
            EntityWorld world = host.World;

            // Both Neutral (never Player2, mirrors PassiveRuntimeTests.NewHost precedent): the float-scoring AI
            // only plays Player2, and Neutral-vs-Neutral is never mutually hostile in CombatSystem, so nothing here
            // fights — the ONLY thing that can move either unit's Health is the furnace passive itself.
            UnitDefinition? forgehandDef = beta.GetUnit("forgehand");  // real furnace_trickle carrier (while_alive)
            Assert.NotNull(forgehandDef);
            Assert.True(forgehandDef!.SelfPassiveAbilityIndex >= 0, "forgehand's furnace_trickle did not resolve to a self-passive slot.");
            int betaUnit = world.Create(V(0, 0, 0), Faction.Neutral,
                Fixed.FromFloat(forgehandDef.Hp), Fixed.FromFloat(forgehandDef.Speed));
            world.ApplyUnitDefinition(betaUnit, forgehandDef); // fires OnUnitDefinitionApplied → installs the HoT

            UnitDefinition? workerDef = alpha.GetUnit("worker"); // real unit; abilities are matter_infusion/mend_matter — neither furnace-type nor while_alive
            Assert.NotNull(workerDef);
            Assert.Equal(-1, workerDef!.SelfPassiveAbilityIndex);
            int alphaUnit = world.Create(V(20, 0, 0), Faction.Neutral,
                Fixed.FromFloat(workerDef.Hp), Fixed.FromFloat(workerDef.Speed));
            world.ApplyUnitDefinition(alphaUnit, workerDef!);

            world.Health[betaUnit]  -= QuarterHpWound(forgehandDef); // pre-damage both below max — derived from each unit's REAL Hp (DW-108)
            world.Health[alphaUnit] -= QuarterHpWound(workerDef);
            Assert.True(world.Health[betaUnit] > Fixed.Zero && world.Health[alphaUnit] > Fixed.Zero,
                "the derived pre-damage must leave both units alive — a dead unit voids the regen isolation under test (DW-108).");
            Fixed betaBefore  = world.Health[betaUnit];
            Fixed alphaBefore = world.Health[alphaUnit];

            for (int i = 0; i < 60; i++) host.StepOnce();

            Assert.True(world.Health[betaUnit] > betaBefore,
                $"Real beta forgehand did not regen off its shipped furnace_trickle passive: {world.Health[betaUnit].Raw} vs before {betaBefore.Raw}");
            Assert.True(world.Health[betaUnit] <= world.EffectiveMaxHealth[betaUnit],
                $"Furnace regen overshot EffectiveMaxHealth: {world.Health[betaUnit].Raw} > {world.EffectiveMaxHealth[betaUnit].Raw}");
            Assert.Equal(alphaBefore.Raw, world.Health[alphaUnit].Raw); // no furnace ability → untouched by the mechanic
        }

        // ── AC2 — Equal Exchange flat, armor-independent self-cost ───────────────────────────────────────────

        [Fact]
        public void SpikeTransmutation_RealAbility_FlatArmorIndependentSelfCost()
        {
            var (alpha, beta, registry) = LoadRealContent();
            SimulationHost host = NewHost(alpha, beta, registry);
            EntityWorld world = host.World;

            int spikeIdx = registry.IndexOf("spike_transmutation");
            Assert.True(spikeIdx >= 0, "spike_transmutation is not in the real loaded AbilityRegistry.");
            AbilityDefinition spike = registry.Get(spikeIdx);
            Assert.Equal(0, spike.CostOre);
            Assert.Equal(0, spike.CostCrystal);

            // Design Notes: ArmorType is never read by the cost_health debit path. The strongest real-content proof
            // pairs the ACTUAL roster unit that carries spike_transmutation — alpha's "infantry" (real ArmorType.Medium,
            // spawned via ApplyUnitDefinition so its AbilityId/ArmorTypeOf come from the shipped JSON, not hand-poked) —
            // against one synthetic caster differing ONLY in ArmorType (alpha's roster has just this one carrier, so a
            // second real unit isn't available to vary armor type).
            UnitDefinition? infantryDef = alpha.GetUnit("infantry");
            Assert.NotNull(infantryDef);
            int casterInfantry = world.Create(V(0, 0, 0), Faction.Player1,
                Fixed.FromFloat(infantryDef!.Hp), Fixed.FromFloat(infantryDef.Speed));
            world.ApplyUnitDefinition(casterInfantry, infantryDef);
            Assert.Equal(ArmorType.Medium, world.ArmorTypeOf[casterInfantry]); // infantry's real, shipped armor_type
            Assert.Equal(spikeIdx, world.AbilityId[casterInfantry * EntityWorld.MAX_ABILITIES_PER_UNIT + 0]);

            int casterHeavy = world.Create(V(10, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            world.AbilityId[casterHeavy * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = spikeIdx;
            world.AbilityCount[casterHeavy] = 1;
            world.ArmorTypeOf[casterHeavy] = ArmorType.Heavy; // synthetic — deliberately differs from infantry's real Medium

            Fixed infantryBefore = world.Health[casterInfantry];
            Fixed heavyBefore = world.Health[casterHeavy];

            IssueSelfCast(world, casterInfantry);
            IssueSelfCast(world, casterHeavy);
            host.StepOnce();

            Fixed infantryDelta = infantryBefore - world.Health[casterInfantry];
            Fixed heavyDelta = heavyBefore - world.Health[casterHeavy];

            Assert.Equal(Fixed.FromInt(spike.CostHealth).Raw, infantryDelta.Raw);
            Assert.Equal(Fixed.FromInt(spike.CostHealth).Raw, heavyDelta.Raw);
            Assert.Equal(infantryDelta.Raw, heavyDelta.Raw); // identical flat delta: real Medium vs synthetic Heavy
        }

        /// <summary>Issue a slot-0 self-cast intent through the shared, real <see cref="OrderApplier"/> (the real
        /// intent path CastHarness.IssueAndTick mirrors) — TargetZ -1 = Self/None per NetworkCommand's convention.</summary>
        private static void IssueSelfCast(EntityWorld world, int casterId) =>
            OrderApplier.Apply(world, new UnitOrder(casterId, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(-1)), Faction.Player1);

        // ── AC3 — two-run determinism over the combined furnace + cast scenario ──────────────────────────────

        [Fact]
        public void SignatureMechanicScenario_TwoRuns_ByteIdenticalChecksums()
        {
            var a = GoldenChecksumReplay.RunAndRecord(100, build: BuildCombinedScenarioHarness);
            var b = GoldenChecksumReplay.RunAndRecord(100, build: BuildCombinedScenarioHarness);

            Assert.True(a.Count >= 100, $"expected >= 100 checksum samples, got {a.Count}");
            Assert.True(a.SequenceEqual(b),
                "Two fresh runs of the signature-mechanic scenario (real furnace regen + real spike_transmutation cast) diverged — a determinism leak.");
        }

        /// <summary>
        /// A FRESH real-content host carrying both mechanics: a real beta furnace_trickle carrier (pre-damaged),
        /// a real alpha control unit with no furnace ability, the real alpha "infantry" spike_transmutation caster,
        /// and one synthetic Heavy-armor caster (differing only in ArmorType) — cast intents ALREADY issued (fired
        /// the same tick as the first <see cref="SimulationHost.StepOnce"/>) — so failure localizes to these two
        /// mechanics, not a 15th committed golden baseline (Design Notes). The Player1 casters are placed 40-50
        /// units from the Neutral furnace/control units — far outside any vision/attack range, and neither caster
        /// receives attack stats via <c>ApplyUnitDefinition</c>/hand-set fields — so nothing here can interact
        /// across factions; the only thing that can move any unit's Health is its own signature mechanic.
        /// </summary>
        private static GoldenHarness BuildCombinedScenarioHarness()
        {
            var (alpha, beta, registry) = LoadRealContent();
            SimulationHost host = NewHost(alpha, beta, registry);
            EntityWorld world = host.World;

            UnitDefinition forgehandDef = beta.GetUnit("forgehand")!;
            int betaUnit = world.Create(V(0, 0, 0), Faction.Neutral,
                Fixed.FromFloat(forgehandDef.Hp), Fixed.FromFloat(forgehandDef.Speed));
            world.ApplyUnitDefinition(betaUnit, forgehandDef);
            world.Health[betaUnit] -= QuarterHpWound(forgehandDef); // derived from the REAL Hp, never a magic magnitude (DW-108)

            UnitDefinition workerDef = alpha.GetUnit("worker")!;
            int alphaUnit = world.Create(V(20, 0, 0), Faction.Neutral,
                Fixed.FromFloat(workerDef.Hp), Fixed.FromFloat(workerDef.Speed));
            world.ApplyUnitDefinition(alphaUnit, workerDef);
            world.Health[alphaUnit] -= QuarterHpWound(workerDef); // derived from the REAL Hp, never a magic magnitude (DW-108)

            int spikeIdx = registry.IndexOf("spike_transmutation");
            UnitDefinition infantryDef = alpha.GetUnit("infantry")!;
            int casterInfantry = world.Create(V(40, 0, 0), Faction.Player1,
                Fixed.FromFloat(infantryDef.Hp), Fixed.FromFloat(infantryDef.Speed));
            world.ApplyUnitDefinition(casterInfantry, infantryDef); // real ArmorType.Medium + AbilityId from shipped JSON

            int casterHeavy = world.Create(V(50, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            world.AbilityId[casterHeavy * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = spikeIdx;
            world.AbilityCount[casterHeavy] = 1;
            world.ArmorTypeOf[casterHeavy] = ArmorType.Heavy; // synthetic — deliberately differs from infantry's real Medium

            IssueSelfCast(world, casterInfantry);
            IssueSelfCast(world, casterHeavy);

            return new GoldenHarness(host, 0);
        }

        // ── AC4 — the on-death Glut aura is structurally impossible to author this epic ───────────────────────

        [Fact]
        public void OnDeathActivation_NotInClosedSet_CannotBeAuthored()
        {
            var def = new AbilityDefinition { Id = "glut_on_death_probe", Activation = "on_death" };

            Assert.Null(def.ParsedActivation); // not a member of the closed PassiveActivation set (active|aura|on_hit|while_alive)

            AbilityValidationResult result = new AbilityValidator().Validate(def);
            Assert.False(result.Ok);
            Assert.Contains("activation", result.Error, StringComparison.Ordinal);
            Assert.Contains("on_death", result.Error, StringComparison.Ordinal);
        }

        // ── Descriptor-fix regression (Code Map) — BOTH factions' ids resolve against the real registry ───────
        // Review pass 1 (Blind Hunter + Edge Case Hunter, converged): the original version of this test only
        // asserted alpha's descriptor, leaving beta's furnace_trickle id unguarded against the exact same
        // dangling-string defect class this story exists to fix.

        [Fact]
        public void SignatureMechanicEffectIds_ResolveInRealRegistry_BothFactions()
        {
            var (alpha, beta, registry) = LoadRealContent();

            Assert.Equal("spike_transmutation", alpha.SignatureMechanicEffectId);
            Assert.True(registry.IndexOf(alpha.SignatureMechanicEffectId!) >= 0,
                $"alpha's signature_mechanic_effect_id '{alpha.SignatureMechanicEffectId}' does not resolve to a real loadable ability.");

            Assert.Equal("furnace_trickle", beta.SignatureMechanicEffectId);
            Assert.True(registry.IndexOf(beta.SignatureMechanicEffectId!) >= 0,
                $"beta's signature_mechanic_effect_id '{beta.SignatureMechanicEffectId}' does not resolve to a real loadable ability.");
        }

        // ── DW-107 hardening teeth — the silent-skip seam LoadRealContent now guards ─────────────────────────

        /// <summary>
        /// Pins the exact trap DW-107 closes, on a synthetic directory (one REAL shipped ability copied
        /// byte-for-byte + one validation-broken file): (a) with <c>onSkipped</c> left at its default the broken
        /// file just vanishes from the registry with no signal — why an unguarded real-content load can silently
        /// test less than shipped content — and (b) the <c>onSkipped</c> channel <see cref="LoadRealContent"/> now
        /// wires actually fires, carrying the offending file name as its diagnostic payload.
        /// </summary>
        [Fact]
        public void LoadFromDirectory_InvalidAbilityFile_ShrinksRegistrySilently_UnlessOnSkippedIsWired()
        {
            string dir = Path.Combine(Path.GetTempPath(), "chimera_dw107_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // Valid: this class's own subject ability, the real shipped spike_transmutation.json.
                File.Copy(Path.Combine(ResolveDataDir("abilities"), "spike_transmutation.json"),
                          Path.Combine(dir, "spike_transmutation.json"));
                // Invalid: "on_death" is outside the closed PassiveActivation set — the same shape
                // OnDeathActivation_NotInClosedSet_CannotBeAuthored proves AbilityValidator rejects.
                File.WriteAllText(Path.Combine(dir, "broken_probe.json"),
                    "{ \"id\": \"broken_probe\", \"activation\": \"on_death\" }");

                AbilityRegistry silent = AbilityRegistry.LoadFromDirectory(dir); // onSkipped defaulted — the DW-107 trap
                Assert.Equal(1, silent.Count); // the broken file vanished with no signal at all
                Assert.True(silent.IndexOf("spike_transmutation") >= 0);
                Assert.Equal(-1, silent.IndexOf("broken_probe"));

                var skipped = new List<string>();
                AbilityRegistry guarded = AbilityRegistry.LoadFromDirectory(dir, skipped.Add);
                Assert.Equal(1, guarded.Count);
                string reported = Assert.Single(skipped);
                Assert.Equal("broken_probe.json", reported); // the file-name diagnostic LoadRealContent surfaces on failure
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>
        /// DW-107 named guard: every test in this class routes through <see cref="LoadRealContent"/>'s skip/floor
        /// asserts, but this fact exists so a shipped-content shrink fails under a name that says exactly that
        /// instead of surfacing first as a confusing mechanic-behavior failure — and it pins the three signature
        /// ability ids this class drives (furnace_pour is otherwise unasserted here) as present in the REAL registry.
        /// </summary>
        [Fact]
        public void LoadRealContent_ShippedAbilityRoster_LoadsCompletely()
        {
            var (_, _, registry) = LoadRealContent(); // fails loud inside on any skip or a sub-floor count

            Assert.True(registry.Count >= MIN_SHIPPED_ABILITY_COUNT,
                $"registry.Count {registry.Count} fell below the shipped floor {MIN_SHIPPED_ABILITY_COUNT}.");
            Assert.True(registry.IndexOf("furnace_trickle") >= 0, "shipped furnace_trickle is missing from the real registry.");
            Assert.True(registry.IndexOf("furnace_pour") >= 0, "shipped furnace_pour is missing from the real registry.");
            Assert.True(registry.IndexOf("spike_transmutation") >= 0, "shipped spike_transmutation is missing from the real registry.");
        }
    }
}
