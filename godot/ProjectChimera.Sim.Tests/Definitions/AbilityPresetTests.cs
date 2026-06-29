#nullable enable
using System.Text.Json;
using ProjectChimera.Core.Definitions;  // AbilityPresets, AbilityValidator, AbilityLoader, ContentJson
using ProjectChimera.Effects;           // SequenceEffect
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.5a (AC1, AC3, AC6b/c) — the closed simple-mode <see cref="AbilityPresets"/> set is sound: every
    /// preset expands to a graph that PASSES <see cref="AbilityValidator"/> (nothing un-runnable ships), every preset
    /// survives a serialize→reparse round-trip with an identical graph, and an invalid graph yields a LOCATED
    /// <c>Fail</c> carrying no runnable <c>Validated&lt;T&gt;</c>.
    /// </summary>
    public class AbilityPresetTests
    {
        [Fact]
        public void EveryPreset_BuildsToAGraph_ThatPassesTheValidator()
        {
            foreach (var (kind, label) in AbilityPresets.All)
            {
                AbilityDefinition def = AbilityPresets.BuildDefault(kind);
                AbilityValidationResult r = new AbilityValidator().Validate(def);
                Assert.True(r.Ok, $"Preset '{label}' ({kind}) failed validation: {r.Error}");
                Assert.NotNull(r.Value.Value.EffectGraph);   // a real effect escaped the gate
            }
        }

        [Fact]
        public void EveryPreset_SurvivesSerializeRoundTrip_WithIdenticalGraph()
        {
            foreach (var (kind, label) in AbilityPresets.All)
            {
                AbilityDefinition def = AbilityPresets.BuildDefault(kind);

                string json = JsonSerializer.Serialize(def, ContentJson.Options);
                AbilityValidationResult r = AbilityLoader.Load(json, def.Id);

                Assert.True(r.Ok, $"Preset '{label}' round-trip failed: {r.Error}");
                Assert.Equal(def.CostEnergy.Raw, r.Value.Value.CostEnergy.Raw);
                Assert.Equal(def.Cooldown.Raw, r.Value.Value.Cooldown.Raw);
                EffectGraphAssert.Equal(def.EffectGraph, r.Value.Value.EffectGraph);
            }
        }

        [Fact]
        public void InvalidGraph_ZeroChildSequence_YieldsLocatedFail_WithNoRunnableValue()
        {
            var def = new AbilityDefinition
            {
                Id          = "broken_seq",
                DisplayName = "Broken",
                Targeting   = "Self",
                EffectGraph = new SequenceEffect(),   // 0 children — an effect must do something (AC3 invalid case)
            };

            AbilityValidationResult r = new AbilityValidator().Validate(def);

            Assert.False(r.Ok);
            Assert.NotNull(r.Error);
            Assert.Contains("broken_seq", r.Error!);   // located: names the ability id
            Assert.Contains("children", r.Error!);     // names the offending node/field
            Assert.Null(r.Value.Value);                // no runnable Validated<AbilityDefinition> escaped the gate
        }
    }
}
