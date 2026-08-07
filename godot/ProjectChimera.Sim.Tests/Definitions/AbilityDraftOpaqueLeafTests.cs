#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-903 — the structured composer must survive the Story 15.13 leaves it has no widgets for.
    ///
    /// <para><b>The defect this pins.</b> Story 15.13 added <c>teleport</c>, <c>play_vfx</c>, <c>play_sound</c> and
    /// <c>shake_screen</c> to the closed vocabulary and shipped <c>blink_strike.json</c> using all four, but never
    /// taught <see cref="DraftNode.FromEffectNode"/> about them. Its default arm THROWS, and nothing on the
    /// <c>LoadFromRegistry → ReflectModelIntoForm → SeedDraftFromDef</c> path catches — so opening that ability in the
    /// Ability Editor took the editor down. A vocabulary addition must therefore always be accompanied by a composer
    /// decision: render it, or carry it opaquely. These four are carried.</para>
    /// </summary>
    public class AbilityDraftOpaqueLeafTests
    {
        public static TheoryData<EffectNode> TheFourNewLeaves() => new()
        {
            new TeleportEffect(),
            new PlayVfxEffect(new CombatFeedbackProfile { HitFlash = new FlashSpec { Scale = 2f } }),
            new PlaySoundEffect(new CombatFeedbackProfile { ImpactSoundId = "zap" }),
            new ShakeScreenEffect(new CombatFeedbackProfile { Shake = new ShakeSpec { DurationSec = 0.2f, Strength = 0.3f } }),
        };

        [Theory]
        [MemberData(nameof(TheFourNewLeaves))]
        public void LoadingALeafTheComposerCannotRender_DoesNotThrow(EffectNode leaf)
        {
            DraftNode draft = DraftNode.FromEffectNode(leaf);   // this THREW before DW-903
            Assert.Equal(DraftKind.Opaque, draft.Kind);
        }

        [Theory]
        [MemberData(nameof(TheFourNewLeaves))]
        public void AnOpaqueLeafMaterializesBackAsTheVerySameNode(EffectNode leaf)
        {
            // Losslessness is the whole point: a composer round-trip must not quietly rewrite (or drop) a node it
            // cannot edit. Reference identity is the strongest available statement of that, and it is safe because
            // EffectNodes are immutable.
            Assert.Same(leaf, DraftNode.FromEffectNode(leaf).ToEffectNode());
        }

        [Fact]
        public void AnOpaqueLeafSurvivesAFullDraftRoundTripInsideASequence()
        {
            var teleport = new TeleportEffect();
            var sound    = new PlaySoundEffect(new CombatFeedbackProfile { ImpactSoundId = "blink_strike" });
            var graph    = new SequenceEffect(teleport, new HealEffect(Fixed.FromInt(5)), sound);

            var def = new AbilityDefinition
            {
                Id = "blink_like", DisplayName = "Blink-like", Targeting = "GroundPoint",
                Activation = "active", EffectGraph = graph,
            };

            AbilityDraft draft = AbilityDraft.FromDefinition(def);
            EffectNode rebuilt = draft.Effect!.ToEffectNode();

            var seq = Assert.IsType<SequenceEffect>(rebuilt);
            Assert.Equal(3, seq.Children.Length);
            Assert.Same(teleport, seq.Children[0]);
            Assert.IsType<HealEffect>(seq.Children[1]);   // the editable neighbour still rebuilds normally
            Assert.Same(sound, seq.Children[2]);
        }

        [Fact]
        public void TheComposerNeverOFFERSTheOpaqueKind()
        {
            // Opaque is arrival-only: there is no way to construct one from scratch, so it must not appear in the
            // authorable vocabulary the panel builds its kind dropdown from.
            Assert.DoesNotContain(DraftKind.Opaque, DraftVocabulary.Kinds);
        }

        [Fact]
        public void ResettingAnOpaqueNodeToAnEditableKindReleasesTheCarriedNode()
        {
            DraftNode draft = DraftNode.FromEffectNode(new TeleportEffect());
            draft.ResetKind(DraftKind.Heal);

            Assert.Equal(DraftKind.Heal, draft.Kind);
            Assert.IsType<HealEffect>(draft.ToEffectNode());   // no stale carry-through from the previous kind
        }

        // NOTE — there is deliberately NO "a genuinely unknown node type still throws" test here. Writing one requires
        // declaring a throwaway EffectNode subclass, and `EffectFoldCompletenessTests.NoConcreteEffectKind_HidesOutsideTheVocabulary`
        // scans every loaded assembly and (correctly) fails on it: the closed vocabulary is closed, test types included.
        // The behaviour is still guaranteed structurally — the four kinds are listed as EXPLICIT switch arms in
        // DraftNode.FromEffectNode rather than folded into the default, so the default's throw remains reachable for
        // whatever is added next.
    }
}
