#nullable enable
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // AbilityPresets, AbilityPresetMatcher, AbilityDefinition
using ProjectChimera.Effects;           // effect shapes + Modifier/StackRule/StatusFlags/TargetFilter
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.5a (gds-code-review patch) — teeth for the LOSSLESS preset detection the Ability Editor relies on for
    /// its Simple/Advanced reconciliation (extracted to the Godot-free <see cref="AbilityPresetMatcher"/>). Three
    /// guarantees, each with a positive AND negative control so none can pass vacuously:
    ///   (1) every preset is detected back to its own kind, and re-building from the recovered params reproduces the
    ///       IDENTICAL graph + header — i.e. "open it in Simple and re-save" never drifts;
    ///   (2) a self-buff-shaped graph whose NON-tunable fields differ from the preset is REFUSED (the data-loss
    ///       footgun the dev closed — e.g. the shipped <c>battle_fury</c>: modifier id 1001, move-speed delta), while
    ///       the exact preset shape IS detected;
    ///   (3) a non-preset graph (Sequence, non-Magic damage, direct_hp_delta, non-Enemy AoE) is REFUSED.
    /// </summary>
    public class AbilityPresetMatcherTests
    {
        // (1) Round-trip: Build(default) → detect → recover → Build(recovered) is graph-identical + header-identical.
        [Fact]
        public void EveryPreset_DetectsBackToItsOwnKind_AndReBuildsIdentically()
        {
            foreach (var (kind, label) in AbilityPresets.All)
            {
                AbilityDefinition original = AbilityPresets.BuildDefault(kind);

                bool detected = AbilityPresetMatcher.TryDetectPreset(original, out AbilityPresets.Kind got, out AbilityPresets.Params recovered);

                Assert.True(detected, $"Preset '{label}' ({kind}) was not detected as a preset.");
                Assert.Equal(kind, got);

                // Re-build from the recovered params: same graph (Fixed.Raw + structure) and same non-tunable header,
                // proving the Simple form carries every field faithfully (no silent drop/rewrite on re-save).
                AbilityDefinition rebuilt = AbilityPresets.Build(got, recovered);
                EffectGraphAssert.Equal(original.EffectGraph, rebuilt.EffectGraph);
                Assert.Equal(original.Targeting, rebuilt.Targeting);
                Assert.Equal(original.CostEnergy.Raw, rebuilt.CostEnergy.Raw);
                Assert.Equal(original.Cooldown.Raw, rebuilt.Cooldown.Raw);
            }
        }

        // (2-) Negative control: the data-loss footgun — a Self-Buff-SHAPED apply_modifier whose non-tunable fields
        //      differ from the preset must NOT be detected, or Simple would silently rewrite it on re-save.
        [Fact]
        public void SelfBuffShaped_WithAnyNonTunableFieldDiffering_IsRefused()
        {
            int presetId = AbilityPresets.SelfBuffModifierId;
            Fixed dmg = Fixed.FromInt(12);   // the tunable attack-damage delta (held constant across the variants)

            // Each row flips EXACTLY ONE non-tunable field off the preset's fixed shape (id, maxStacks, stacking,
            // status, max-health delta, move-speed delta, a period effect) → each must flip detection to false.
            var cases = new (string Label, Modifier M)[]
            {
                ("different id (battle_fury 1001)", new Modifier(1001, 150, StackRule.Refresh, 1, Fixed.Zero, dmg, Fixed.Zero, StatusFlags.None, null, 0)),
                ("maxStacks != 1",                  new Modifier(presetId, 150, StackRule.Refresh, 3, Fixed.Zero, dmg, Fixed.Zero, StatusFlags.None, null, 0)),
                ("stacking != Refresh",             new Modifier(presetId, 150, StackRule.Stack,   1, Fixed.Zero, dmg, Fixed.Zero, StatusFlags.None, null, 0)),
                ("status != None",                  new Modifier(presetId, 150, StackRule.Refresh, 1, Fixed.Zero, dmg, Fixed.Zero, StatusFlags.Stunned, null, 0)),
                ("max-health delta present",        new Modifier(presetId, 150, StackRule.Refresh, 1, Fixed.FromInt(5), dmg, Fixed.Zero, StatusFlags.None, null, 0)),
                ("move-speed delta present",        new Modifier(presetId, 150, StackRule.Refresh, 1, Fixed.Zero, dmg, Fixed.FromInt(1), StatusFlags.None, null, 0)),
                ("period effect present",           new Modifier(presetId, 150, StackRule.Refresh, 1, Fixed.Zero, dmg, Fixed.Zero, StatusFlags.None, new HealEffect(Fixed.FromInt(3)), 10)),
            };

            foreach (var (label, m) in cases)
            {
                var def = new AbilityDefinition { Id = "x", DisplayName = "X", Targeting = "Self", EffectGraph = new ApplyModifierEffect(m) };
                Assert.False(AbilityPresetMatcher.TryDetectPreset(def, out _, out _),
                    $"Self-buff variant '{label}' is NOT losslessly Simple-representable and must stay in raw JSON.");
            }
        }

        // (2+) Positive control: the EXACT preset modifier shape (only attack-damage + duration tuned) IS detected and
        //      recovers both tunables — so the guard above isn't vacuously rejecting everything.
        [Fact]
        public void SelfBuff_ExactPresetShape_IsDetected_AndRecoversTunables()
        {
            var def = new AbilityDefinition
            {
                Id = "buff", DisplayName = "Buff", Targeting = "Self",
                EffectGraph = new ApplyModifierEffect(new Modifier(
                    AbilityPresets.SelfBuffModifierId, 999, StackRule.Refresh, 1,
                    Fixed.Zero, Fixed.FromInt(7), Fixed.Zero, StatusFlags.None, null, 0)),
            };

            bool detected = AbilityPresetMatcher.TryDetectPreset(def, out AbilityPresets.Kind kind, out AbilityPresets.Params p);

            Assert.True(detected);
            Assert.Equal(AbilityPresets.Kind.SelfBuff, kind);
            Assert.Equal(Fixed.FromInt(7).Raw, p.Amount.Raw);   // tunable attack-damage delta recovered
            Assert.Equal(999, p.DurationTicks);                 // tunable duration recovered
        }

        // (3) Graphs with no lossless Simple form are refused (stay in the raw-JSON pane).
        [Fact]
        public void NonPresetGraphs_AreRefused()
        {
            var cases = new (string Label, EffectNode Graph)[]
            {
                ("a Sequence (no preset is a Sequence)",        new SequenceEffect(new HealEffect(Fixed.FromInt(10)))),
                ("damage but not Magic",                        new DamageEffect(Fixed.FromInt(80), DamageType.Pierce)),
                ("direct_hp_delta (no preset uses it)",         new DirectHpDeltaEffect(Fixed.FromInt(-10))),
                ("AoE shape but filter != Enemy",               new SearchAreaEffect(Fixed.FromInt(4), TargetFilter.Ally, new DamageEffect(Fixed.FromInt(30), DamageType.Magic))),
                ("AoE shape but child damage != Magic",         new SearchAreaEffect(Fixed.FromInt(4), TargetFilter.Enemy, new DamageEffect(Fixed.FromInt(30), DamageType.Pierce))),
            };

            foreach (var (label, graph) in cases)
            {
                var def = new AbilityDefinition { Id = "x", DisplayName = "X", Targeting = "Self", EffectGraph = graph };
                Assert.False(AbilityPresetMatcher.TryDetectPreset(def, out _, out _),
                    $"Graph '{label}' has no lossless Simple form and must be refused.");
            }
        }
    }
}
