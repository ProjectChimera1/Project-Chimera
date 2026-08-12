#nullable enable
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Effects;           // closed 2.1 effect vocabulary + Modifier
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Recursive structural equality for the closed 2.1 effect graph (Story 2.5a round-trip coverage): asserts the
    /// same sealed node KIND (runtime type), the same <c>Fixed.Raw</c> magnitudes + enum values per kind, and the
    /// same children (order-sensitive for <see cref="SequenceEffect"/>). Pins <c>Fixed</c> by <c>.Raw</c> (the
    /// quantized 16.16 integer) so round-trip identity is judged on the canonical value, never <c>ToFloat</c>.
    /// Covers all 7 registered node kinds + the <see cref="Modifier"/> payload.
    /// </summary>
    internal static class EffectGraphAssert
    {
        public static void Equal(EffectNode? expected, EffectNode? actual)
        {
            if (expected is null) { Assert.Null(actual); return; }
            Assert.NotNull(actual);
            Assert.Equal(expected.GetType(), actual!.GetType());   // node KIND

            switch (expected)
            {
                case DirectHpDeltaEffect e:
                    Assert.Equal(e.Delta.Raw, ((DirectHpDeltaEffect)actual).Delta.Raw);
                    break;

                case HealEffect e:
                    Assert.Equal(e.Amount.Raw, ((HealEffect)actual).Amount.Raw);
                    break;

                case DamageEffect e:
                    var d = (DamageEffect)actual;
                    Assert.Equal(e.Amount.Raw, d.Amount.Raw);
                    Assert.Equal(e.Type, d.Type);                  // DamageType enum (accessor is .Type, NOT .DamageType)
                    break;

                case ApplyModifierEffect e:
                    EqualModifier(e.Modifier, ((ApplyModifierEffect)actual).Modifier);
                    break;

                case SequenceEffect e:
                    var s = (SequenceEffect)actual;
                    Assert.Equal(e.Children.Length, s.Children.Length);
                    for (int i = 0; i < e.Children.Length; i++)
                        Equal(e.Children[i], s.Children[i]);       // recurse, ORDER-sensitive
                    break;

                case SearchAreaEffect e:
                    var sa = (SearchAreaEffect)actual;
                    Assert.Equal(e.Radius.Raw, sa.Radius.Raw);
                    Assert.Equal(e.Filter, sa.Filter);             // TargetFilter flags
                    Equal(e.Child, sa.Child);
                    break;

                case PersistentEffect e:
                    var p = (PersistentEffect)actual;
                    Assert.Equal(e.PeriodTicks, p.PeriodTicks);
                    Assert.Equal(e.PeriodCount, p.PeriodCount);
                    // DW-323: `lifelong` was NOT compared here, which is a large part of why the composer could drop it
                    // on a draft round-trip with every round-trip test green. Compared now, so any future path that
                    // silently defaults it away turns this whole family RED.
                    Assert.Equal(e.Lifelong, p.Lifelong);
                    Equal(e.InitialEffect, p.InitialEffect);       // optional children (null-safe at the top)
                    Equal(e.PeriodEffect,  p.PeriodEffect);
                    Equal(e.ExpireEffect,  p.ExpireEffect);
                    break;

                default:
                    Assert.Fail($"Unhandled effect node kind in EffectGraphAssert: {expected.GetType().Name}");
                    break;
            }
        }

        private static void EqualModifier(Modifier e, Modifier a)
        {
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.DurationTicks, a.DurationTicks);
            Assert.Equal(e.Stacking, a.Stacking);                  // StackRule
            Assert.Equal(e.MaxStacks, a.MaxStacks);
            Assert.Equal(e.MaxHealthDelta.Raw,    a.MaxHealthDelta.Raw);
            Assert.Equal(e.AttackDamageDelta.Raw, a.AttackDamageDelta.Raw);
            Assert.Equal(e.MoveSpeedDelta.Raw,    a.MoveSpeedDelta.Raw);
            Assert.Equal(e.ArmorDelta.Raw,        a.ArmorDelta.Raw);   // Story 2.6
            Assert.Equal(e.Status, a.Status);                      // StatusFlags
            Assert.Equal(e.PeriodTicks, a.PeriodTicks);
            // DW-323 (same omission class as PersistentEffect.Lifelong above): periodic_stack_mode is semantic state
            // the converter and the draft both carry, and nothing compared it on a round-trip.
            Assert.Equal(e.PeriodicStacking, a.PeriodicStacking);  // PeriodicStackMode (DW-272 / Story 15.12)
            Equal(e.PeriodEffect, a.PeriodEffect);                 // nested DoT/HoT graph
        }
    }
}
