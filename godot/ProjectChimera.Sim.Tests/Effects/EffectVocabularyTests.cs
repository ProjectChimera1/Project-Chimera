#nullable enable
using System;
using System.Linq;
using System.Reflection;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.1 AC1 — the closedness contract of the one effect vocabulary. A reflection scan over the
    /// <c>ProjectChimera.Effects</c> assembly proves: every concrete node is sealed; there are EXACTLY three
    /// composition nodes; a first-class <see cref="Modifier"/> exists and is NOT a node; and no node carries a
    /// scripting hook (Delegate/Func/Action), an open <c>object</c> payload, or a <c>float</c>/<c>double</c>/Godot
    /// field. Teeth: this turns RED the moment anyone adds an open/virtual/scripted node or a fourth composition
    /// type (demonstrated by inject-revert in the Dev Agent Record).
    /// </summary>
    public class EffectVocabularyTests
    {
        private static Type[] ConcreteNodes() =>
            typeof(EffectNode).Assembly.GetTypes()
                .Where(t => typeof(EffectNode).IsAssignableFrom(t) && !t.IsAbstract)
                .ToArray();

        [Fact]
        public void EveryConcreteEffectNode_IsSealed()
        {
            Type[] nodes = ConcreteNodes();
            Assert.NotEmpty(nodes); // the scan is actually finding the vocabulary
            foreach (Type t in nodes)
                Assert.True(t.IsSealed,
                    $"{t.Name} is a concrete EffectNode but not sealed — an open/virtual extension point (AC1).");
        }

        [Fact]
        public void ExactlyThreeCompositionNodes_SequenceSearchPersistent()
        {
            Type[] comps = typeof(EffectNode).Assembly.GetTypes()
                .Where(t => typeof(CompositionEffect).IsAssignableFrom(t) && !t.IsAbstract)
                .ToArray();

            Assert.Equal(3, comps.Length);
            Assert.Contains(typeof(SequenceEffect), comps);
            Assert.Contains(typeof(SearchAreaEffect), comps);
            Assert.Contains(typeof(PersistentEffect), comps);
        }

        [Fact]
        public void FirstClassModifier_Exists_AndIsNotAnEffectNode()
        {
            // The type exists (compiles) and is a first-class descriptor, not a leaf/composition node.
            Assert.False(typeof(EffectNode).IsAssignableFrom(typeof(Modifier)),
                "Modifier must be a first-class descriptor, never an EffectNode (AC1).");
        }

        [Fact]
        public void NoNode_ExposesScriptingHook_OrFloat_OrGodot_OrOpenPayload()
        {
            foreach (Type t in ConcreteNodes())
            {
                foreach (FieldInfo f in t.GetFields(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Type ft = f.FieldType;

                    Assert.False(typeof(Delegate).IsAssignableFrom(ft),
                        $"{t.Name}.{f.Name} is a delegate ({ft.Name}) — a scripting escape hatch (AC1).");
                    Assert.False(ft == typeof(object),
                        $"{t.Name}.{f.Name} is System.Object — an open payload (AC1).");
                    Assert.False(ft == typeof(float) || ft == typeof(double),
                        $"{t.Name}.{f.Name} is {ft.Name} — nondeterministic; sim uses Fixed (AC1).");
                    Assert.False(ft.Namespace is not null && ft.Namespace.StartsWith("Godot", StringComparison.Ordinal),
                        $"{t.Name}.{f.Name} references Godot ({ft.FullName}) — the sim layer is Godot-free (AC1).");
                }
            }
        }
    }
}
