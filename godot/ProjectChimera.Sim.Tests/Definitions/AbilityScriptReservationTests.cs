#nullable enable
using System;
using System.Linq;
using System.Reflection;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.3 AC3 — no script payload ever, and the AR-13 random-effect rule owned + discharged by RESERVATION.
    ///
    /// The TESTABLE half is the no-script guarantee, structural from the closed registry: a scripting/eval/random
    /// <c>kind</c> simply isn't registered, so it's rejected as unknown (the converter has no open extension point),
    /// AND no runtime effect node carries a Delegate/Func/object field (re-asserting the 2.1 closedness reasoning at
    /// the converter's boundary). AR-13's "a random effect requires SimRng" is discharged by reservation: the 2.1
    /// vocabulary has no random leaf (so a random kind is unauthorable today), and <see cref="EntityWorld.Rng"/> is
    /// unconditionally present — the mature accept-if-present / reject-if-absent check lands with the story that
    /// first adds a random leaf (the Story 1.7 precedent).
    /// </summary>
    public class AbilityScriptReservationTests
    {
        [Theory]
        [InlineData("lua")]
        [InlineData("run_script")]
        [InlineData("eval")]
        [InlineData("random_pick")] // a hypothetical random leaf — unauthorable today (reservation has teeth)
        public void ScriptOrRandomKind_IsRejectedAsUnknown(string kind)
        {
            string json = $$"""{ "id": "x", "targeting": "Self", "effect": { "kind": "{{kind}}" } }""";
            AbilityValidationResult r = AbilityLoader.Load(json, "x");
            Assert.False(r.Ok);
            Assert.Contains($"unknown kind '{kind}'", r.Error!);
        }

        [Fact]
        public void NoRuntimeEffectNode_ExposesAScriptingHookOrOpenPayload()
        {
            // The registry only maps the sealed 2.1 types; none may carry a delegate/object/free-text-code field
            // (the structural "no scripting escape hatch", AR-22/AC3). Mirrors 2.1's EffectVocabularyTests scan.
            Type[] nodes = typeof(EffectNode).Assembly.GetTypes()
                .Where(t => typeof(EffectNode).IsAssignableFrom(t) && !t.IsAbstract)
                .ToArray();
            Assert.NotEmpty(nodes);

            foreach (Type t in nodes)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.False(typeof(Delegate).IsAssignableFrom(f.FieldType),
                        $"{t.Name}.{f.Name} is a delegate — a scripting escape hatch (AC3).");
                    Assert.False(f.FieldType == typeof(object),
                        $"{t.Name}.{f.Name} is System.Object — an open payload (AC3).");
                }
        }

        [Fact]
        public void SimRng_IsUnconditionallyPresent()
        {
            // AR-13's failing condition ("SimRng absent") can never occur — EntityWorld.Rng is a non-null class.
            var w = new EntityWorld();
            Assert.NotNull(w.Rng);
        }
    }
}
