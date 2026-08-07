#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProjectChimera.Core;              // Fixed, UnitTag
using ProjectChimera.Core.Definitions;  // CanonicalFold (internal — sim sources compile into this assembly)
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Effects;           // EffectNode, LeafEffect, Modifier, effect kinds
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// DW-449 — the EFFECT-fold completeness guard, the effect-subtype analogue of
    /// <see cref="ContentFoldCompletenessTests"/> (which guards the def POCOs only). Two halves:
    ///
    /// <para><b>1. Structural guard.</b> <see cref="CanonicalFold.MixEffect"/>'s explicit arms fold a hand-maintained
    /// field list per kind, and <see cref="CanonicalFold.MixModifier"/> folds a hand-maintained <see cref="Modifier"/>
    /// field list. Nothing used to force those lists to stay complete: a new effect KIND or a new FIELD on an existing
    /// kind/Modifier could move the sim but not the handshake hash — modded content passing the lobby gate and
    /// desyncing mid-match. The classification tests below turn RED the moment a kind or field appears without a
    /// conscious fold decision. (Per-field fold ACTUALITY for the shipped kinds is proven separately by
    /// <c>CanonicalModelHashEffectFoldTests</c>.)</para>
    ///
    /// <para><b>2. Default-arm value fold.</b> The old default arm folded only <c>GetType().Name</c> (value-blind).
    /// The probe tests below define a fake "future" kind (possible because the sim sources compile INTO this test
    /// assembly, so <see cref="LeafEffect"/>'s <c>private protected</c> ctor is reachable) and pin that every field
    /// VALUE of an unknown kind now moves the fold — these fail against the old name-only arm — plus the fail-closed
    /// behaviour for a member type the fold cannot make value-visible.</para>
    /// </summary>
    public class EffectFoldCompletenessTests
    {
        // ── The classification: mirrors CanonicalFold.MixEffect's explicit arms ──────────────────────────────
        // (Folded field lists per kind; EXCLUDED would list consciously-unfolded presentation-only fields — none
        // exist today. Public instance fields, inherited included, because the vocabulary is field-shaped.)

        private static readonly Dictionary<Type, string[]> FoldedFieldsByKind = new Dictionary<Type, string[]>
        {
            [typeof(DirectHpDeltaEffect)] = new[] { "Delta", "RequireTag" },
            [typeof(HealEffect)]          = new[] { "Amount", "RequireTag" },
            [typeof(DamageEffect)]        = new[] { "Amount", "Type", "RequireTag" },
            [typeof(ApplyModifierEffect)] = new[] { "Modifier", "RequireTag" },
            [typeof(SequenceEffect)]      = new[] { "Children" },
            [typeof(SearchAreaEffect)]    = new[] { "Radius", "Filter", "Child", "RequireTag" },
            [typeof(PersistentEffect)]    = new[] { "InitialEffect", "PeriodEffect", "ExpireEffect", "PeriodTicks", "PeriodCount", "Lifelong" },
        };

        private static readonly string[] ModifierFoldedFields =
        {
            "Id", "DurationTicks", "Stacking", "MaxStacks", "MaxHealthDelta", "AttackDamageDelta",
            "MoveSpeedDelta", "ArmorDelta", "Status", "PeriodEffect", "PeriodTicks",
            "PeriodicStacking", // DW-272 / Story 15.12 (folded by name in MixModifier; CanonicalModelHash AlgoVersion 14→15)
        };

        private static Type[] ConcreteNodes() =>
            typeof(EffectNode).Assembly.GetTypes()
                .Where(t => typeof(EffectNode).IsAssignableFrom(t) && !t.IsAbstract)
                .ToArray();

        // ── 1a. Kind-set guard: every SHIPPED concrete kind has an explicit MixEffect arm ────────────────────

        [Fact]
        public void EveryShippedEffectKind_HasAnExplicitFoldClassification()
        {
            Type[] shipped = ConcreteNodes()
                .Where(t => t.Namespace == "ProjectChimera.Effects")
                .ToArray();
            Assert.NotEmpty(shipped); // non-vacuity: the scan really sees the vocabulary

            string[] unclassified = shipped.Where(t => !FoldedFieldsByKind.ContainsKey(t))
                .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(unclassified.Length == 0,
                $"Effect kind(s) [{string.Join(", ", unclassified)}] have no explicit CanonicalFold.MixEffect arm " +
                "classification. A new kind must get an explicit arm (folding every semantic field) plus an entry in " +
                "FoldedFieldsByKind — the reflection default arm is a safety net, not the contract (DW-449).");

            string[] stale = FoldedFieldsByKind.Keys.Where(t => !shipped.Contains(t))
                .Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(stale.Length == 0,
                $"Classified kind(s) [{string.Join(", ", stale)}] are no longer shipped concrete EffectNode subtypes " +
                "in ProjectChimera.Effects — update the guard (and CanonicalFold.MixEffect's arms).");
        }

        [Fact]
        public void NoConcreteEffectKind_HidesOutsideTheVocabularyNamespace()
        {
            // The kind-set guard scopes to ProjectChimera.Effects; this companion closes the escape hatch: any
            // concrete node OUTSIDE that namespace must be one of this suite's explicit test probes.
            Type[] probes = { typeof(ProbeFutureEffect), typeof(ProbeFutureTwinEffect), typeof(ProbeUnfoldableEffect) };
            string[] strays = ConcreteNodes()
                .Where(t => t.Namespace != "ProjectChimera.Effects" && !probes.Contains(t))
                .Select(t => t.FullName ?? t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(strays.Length == 0,
                $"Concrete EffectNode subtype(s) [{string.Join(", ", strays)}] live outside ProjectChimera.Effects — " +
                "move them into the vocabulary namespace so the DW-449 fold-completeness guard covers them.");
        }

        // ── 1b. Per-kind field guard: a new/renamed field on a shipped kind is a conscious fold decision ─────

        [Fact]
        public void EveryShippedEffectKindField_IsConsciouslyClassified()
        {
            foreach (KeyValuePair<Type, string[]> kv in FoldedFieldsByKind)
                AssertFieldsClassified(kv.Key, kv.Value);
        }

        [Fact]
        public void EveryModifierField_IsConsciouslyClassified()
        {
            Assert.True(typeof(Modifier).IsSealed,
                "Modifier must stay sealed — MixModifier folds the concrete Modifier only; a subtype would escape it.");
            AssertFieldsClassified(typeof(Modifier), ModifierFoldedFields);
        }

        private static void AssertFieldsClassified(Type t, string[] folded)
        {
            string[] actual = t.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name).ToArray();
            Assert.NotEmpty(actual); // non-vacuity

            string[] unclassified = actual.Where(n => !folded.Contains(n, StringComparer.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(unclassified.Length == 0,
                $"{t.Name}: public field(s) [{string.Join(", ", unclassified)}] are not in the folded classification. " +
                "Fold them in the kind's explicit CanonicalFold arm (and CanonicalModelHashEffectFoldTests) and add " +
                "them here — a semantic field must not silently escape the handshake hash (DW-449).");

            string[] stale = folded.Where(n => !actual.Contains(n, StringComparer.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(stale.Length == 0,
                $"{t.Name}: classified field(s) [{string.Join(", ", stale)}] no longer exist — update the guard " +
                "(and the kind's CanonicalFold arm).");

            // The vocabulary is public-readonly-FIELD-shaped; a property-shaped stat would bypass a fields-only
            // guard, so its appearance must also be a conscious decision.
            string[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(props.Length == 0,
                $"{t.Name}: public instance property(ies) [{string.Join(", ", props)}] found — the effect vocabulary " +
                "is field-shaped. If a property-shaped stat is intended, fold it explicitly and extend this guard.");
        }

        // ── 2. Default-arm probes: an UNKNOWN kind folds by VALUE (fails against the old name-only arm) ──────

        /// <summary>A fake "future" effect kind with no explicit MixEffect arm — reaches the default arm. Sealed,
        /// field-shaped, no float/object/delegate/Godot members, so the EffectVocabularyTests closedness scans stay
        /// green. Inert: Apply is a no-op and nothing ever executes it.</summary>
        private sealed class ProbeFutureEffect : LeafEffect
        {
            public readonly Fixed Magnitude;
            public readonly int Charges;
            public readonly bool Piercing;
            public readonly string? Note;
            public readonly DamageType Flavor;
            public readonly EffectNode? Child;

            public ProbeFutureEffect(Fixed magnitude, int charges = 3, bool piercing = false, string? note = null,
                                     DamageType flavor = DamageType.Normal, EffectNode? child = null,
                                     UnitTag requireTag = UnitTag.None)
                : base(requireTag)
            {
                Magnitude = magnitude;
                Charges = charges;
                Piercing = piercing;
                Note = note;
                Flavor = flavor;
                Child = child;
            }

            internal override void Apply(in EffectContext ctx) { } // inert probe
        }

        /// <summary>The IDENTICAL member layout (names, types, defaults) as <see cref="ProbeFutureEffect"/>, only the
        /// KIND differs — so the discriminator pin below cannot pass by member-count/member-name differences alone.</summary>
        private sealed class ProbeFutureTwinEffect : LeafEffect
        {
            public readonly Fixed Magnitude;
            public readonly int Charges;
            public readonly bool Piercing;
            public readonly string? Note;
            public readonly DamageType Flavor;
            public readonly EffectNode? Child;

            public ProbeFutureTwinEffect(Fixed magnitude, int charges = 3, bool piercing = false, string? note = null,
                                         DamageType flavor = DamageType.Normal, EffectNode? child = null,
                                         UnitTag requireTag = UnitTag.None)
                : base(requireTag)
            {
                Magnitude = magnitude;
                Charges = charges;
                Piercing = piercing;
                Note = note;
                Flavor = flavor;
                Child = child;
            }

            internal override void Apply(in EffectContext ctx) { } // inert probe
        }

        /// <summary>Carries a member type the value fold cannot handle — must FAIL CLOSED, never hash value-blind.
        /// (List&lt;int&gt; is vocabulary-scan-clean: not a delegate/object/float/Godot type.)</summary>
        private sealed class ProbeUnfoldableEffect : LeafEffect
        {
            public readonly List<int> Payload;

            public ProbeUnfoldableEffect() : base(UnitTag.None) { Payload = new List<int> { 1 }; }

            internal override void Apply(in EffectContext ctx) { } // inert probe
        }

        private static ulong Fold(EffectNode e) => CanonicalFold.MixEffect(CanonicalFold.Offset, e);

        private static ProbeFutureEffect Base() => new ProbeFutureEffect(Fixed.FromInt(7));

        [Fact]
        public void UnknownKind_FixedField_MovesTheFold() =>
            Assert.NotEqual(Fold(Base()), Fold(new ProbeFutureEffect(Fixed.FromInt(9))));

        [Fact]
        public void UnknownKind_IntField_MovesTheFold() =>
            Assert.NotEqual(Fold(Base()), Fold(new ProbeFutureEffect(Fixed.FromInt(7), charges: 4)));

        [Fact]
        public void UnknownKind_BoolField_MovesTheFold() =>
            Assert.NotEqual(Fold(Base()), Fold(new ProbeFutureEffect(Fixed.FromInt(7), piercing: true)));

        [Fact]
        public void UnknownKind_StringField_MovesTheFold() =>
            Assert.NotEqual(Fold(new ProbeFutureEffect(Fixed.FromInt(7), note: "x")),
                            Fold(new ProbeFutureEffect(Fixed.FromInt(7), note: "y")));

        [Fact]
        public void UnknownKind_NullString_DoesNotAliasEmptyString() =>
            Assert.NotEqual(Fold(new ProbeFutureEffect(Fixed.FromInt(7), note: null)),
                            Fold(new ProbeFutureEffect(Fixed.FromInt(7), note: "")));

        [Fact]
        public void UnknownKind_EnumField_FoldsByName() =>
            Assert.NotEqual(Fold(Base()), Fold(new ProbeFutureEffect(Fixed.FromInt(7), flavor: DamageType.Pierce)));

        [Fact]
        public void UnknownKind_InheritedRequireTagField_MovesTheFold() =>
            Assert.NotEqual(Fold(Base()), Fold(new ProbeFutureEffect(Fixed.FromInt(7), requireTag: UnitTag.Organic)));

        [Fact]
        public void UnknownKind_ChildPresence_MovesTheFold() =>
            Assert.NotEqual(Fold(Base()),
                            Fold(new ProbeFutureEffect(Fixed.FromInt(7), child: new HealEffect(Fixed.FromInt(1)))));

        [Fact]
        public void UnknownKind_ChildValue_FoldsRecursively() =>
            // Not just presence: the child's own payload must fold through the shared walk.
            Assert.NotEqual(Fold(new ProbeFutureEffect(Fixed.FromInt(7), child: new HealEffect(Fixed.FromInt(1)))),
                            Fold(new ProbeFutureEffect(Fixed.FromInt(7), child: new HealEffect(Fixed.FromInt(2)))));

        [Fact]
        public void UnknownKind_KindDiscriminator_StillFolds() =>
            // Identical member names/values, different kind ⇒ different fold (the old arm's one guarantee, kept).
            Assert.NotEqual(Fold(new ProbeFutureEffect(Fixed.FromInt(7), charges: 3)),
                            Fold(new ProbeFutureTwinEffect(Fixed.FromInt(7), charges: 3)));

        [Fact]
        public void UnknownKind_Fold_IsRepeatable()
        {
            // The sorted member walk is deterministic: same value ⇒ same fold, across instances and calls.
            var a = new ProbeFutureEffect(Fixed.FromInt(7), charges: 5, note: "n", child: new HealEffect(Fixed.FromInt(1)));
            var b = new ProbeFutureEffect(Fixed.FromInt(7), charges: 5, note: "n", child: new HealEffect(Fixed.FromInt(1)));
            Assert.Equal(Fold(a), Fold(a));
            Assert.Equal(Fold(a), Fold(b));
        }

        [Fact]
        public void UnknownKind_UnfoldableMemberType_FailsClosed()
        {
            NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Fold(new ProbeUnfoldableEffect()));
            Assert.Contains("ProbeUnfoldableEffect", ex.Message);
        }

        [Fact]
        public void KnownKinds_NeverTakeTheDefaultArm_FoldUnchanged()
        {
            // The reflection arm is DEAD for shipped kinds — their explicit arms pin the historic fold bytes (the
            // hero-start-state golden guards this end-to-end; this is the cheap local pin that a shipped kind's fold
            // does NOT route through the member-name walk, which would fold e.g. "Amount" into the hash).
            ulong h = Fold(new HealEffect(Fixed.FromInt(5)));
            ulong expected = CanonicalFold.MixInt(CanonicalFold.Offset, 1);      // present marker
            expected = CanonicalFold.MixStr(expected, "heal");                    // kind string
            expected = CanonicalFold.MixInt(expected, Fixed.FromInt(5).Raw);      // Amount.Raw
            expected = CanonicalFold.MixStr(expected, UnitTag.None.ToString());   // RequireTag by name
            Assert.Equal(expected, h);
        }
    }
}
