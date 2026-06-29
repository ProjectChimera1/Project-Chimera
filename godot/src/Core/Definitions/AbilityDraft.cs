#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Combat;   // DamageType
using ProjectChimera.Core;     // Fixed
using ProjectChimera.Effects;  // the closed 2.1 effect vocabulary + Modifier + StackRule/StatusFlags/TargetFilter

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The 7 authorable effect kinds — the closed vocabulary the Ability Editor's structured composer offers
    /// (AR-13 / AC5-COMPOSER, Story 2.5b). EXACTLY mirrors the converter's closed <c>"kind"</c> registry
    /// (<see cref="EffectNodeJsonConverter"/>): the editor offers these and <b>no eighth</b>. The abstract bases
    /// (<c>EffectNode</c>/<c>LeafEffect</c>/<c>CompositionEffect</c>) are not authorable and are absent here.
    /// </summary>
    public enum DraftKind : byte
    {
        DirectHpDelta = 0,
        Heal          = 1,
        Damage        = 2,
        ApplyModifier = 3,
        Sequence      = 4,
        SearchArea    = 5,
        Persistent    = 6,
    }

    /// <summary>
    /// The closed sets of values the structured composer is allowed to OFFER on its new dropdowns (AC5-COMPOSER).
    /// Hosted Godot-free so the closed-vocabulary guarantee is Tier-1-testable (and the panel builds its dropdowns
    /// from these — one source of truth, no parallel list to drift): the <c>damage_type</c> set excludes the
    /// internal sentinel <see cref="DamageType.COUNT"/>; the <c>filter</c> set excludes the reserved
    /// <see cref="TargetFilter.Air"/>/<see cref="TargetFilter.Ground"/>/<see cref="TargetFilter.Structure"/> bits
    /// (Story 2.9a). Because the composer can only ever CONSTRUCT a node from these, it can never build a node the
    /// converter's <c>Read</c> rejects (the load-bearing AC5 defense — Decision #9's Write-side guard kept at its
    /// default OFF, so this list IS the guarantee).
    /// </summary>
    public static class DraftVocabulary
    {
        /// <summary>The closed 7 authorable kinds, in author-facing order.</summary>
        public static readonly DraftKind[] Kinds =
        {
            DraftKind.DirectHpDelta, DraftKind.Heal, DraftKind.Damage, DraftKind.ApplyModifier,
            DraftKind.Sequence, DraftKind.SearchArea, DraftKind.Persistent,
        };

        /// <summary>Authorable damage types — the 5 real types; the internal <see cref="DamageType.COUNT"/> sentinel is excluded.</summary>
        public static readonly DamageType[] DamageTypes =
        {
            DamageType.Normal, DamageType.Pierce, DamageType.Siege, DamageType.Magic, DamageType.Hero,
        };

        /// <summary>Authorable target filters — allegiance + Alive; the reserved Air/Ground/Structure bits are excluded (Story 2.9a).</summary>
        public static readonly TargetFilter[] Filters =
        {
            TargetFilter.None, TargetFilter.Self, TargetFilter.Ally, TargetFilter.Enemy,
            TargetFilter.Neutral, TargetFilter.Alive,
        };

        /// <summary>Authorable stacking rules (the full closed <see cref="StackRule"/> set).</summary>
        public static readonly StackRule[] StackRules = { StackRule.Refresh, StackRule.Stack, StackRule.Ignore };

        /// <summary>Authorable status flags (the full closed <see cref="StatusFlags"/> set).</summary>
        public static readonly StatusFlags[] Statuses =
        {
            StatusFlags.None, StatusFlags.Stunned, StatusFlags.Rooted,
            StatusFlags.Silenced, StatusFlags.Disarmed, StatusFlags.Invulnerable,
        };
    }

    /// <summary>
    /// A MUTABLE authoring/scratch model for a <see cref="Modifier"/> (the payload of an <c>apply_modifier</c> node).
    /// The runtime <see cref="Modifier"/> is immutable (ctor-only), so the composer edits this mutable record and
    /// MATERIALIZES a fresh immutable <see cref="Modifier"/> via <see cref="ToModifier"/> at serialize/validate time.
    /// Defaults mirror the converter's read-fallbacks (<see cref="EffectNodeJsonConverter"/>) so a freshly-built
    /// modifier round-trips stably: <c>MaxStacks=1</c>, <c>Stacking=Refresh</c>, <c>Status=None</c>, deltas zero.
    /// </summary>
    public sealed class DraftModifier
    {
        public int         Id               = 1;                  // author-assigned id; 1 is a sensible start (matches AbilityPresets.SelfBuffModifierId)
        public int         DurationTicks;                          // <0 = permanent, 0 = one-shot
        public StackRule   Stacking         = StackRule.Refresh;   // converter read-fallback
        public int         MaxStacks        = 1;                   // converter read-fallback
        public Fixed       MaxHealthDelta   = Fixed.Zero;
        public Fixed       AttackDamageDelta = Fixed.Zero;
        public Fixed       MoveSpeedDelta   = Fixed.Zero;
        public StatusFlags Status           = StatusFlags.None;
        public DraftNode?  Period;                                 // optional period_effect (DoT/HoT)
        public int         PeriodTicks;

        /// <summary>Materialize the immutable runtime <see cref="Modifier"/> (constructor order is load-bearing — verified).</summary>
        public Modifier ToModifier() => new Modifier(
            Id, DurationTicks, Stacking, MaxStacks,
            MaxHealthDelta, AttackDamageDelta, MoveSpeedDelta,
            Status, Period?.ToEffectNode(), PeriodTicks);

        /// <summary>Build a mutable draft from an existing immutable runtime <see cref="Modifier"/> (parse-in / load path).</summary>
        public static DraftModifier FromModifier(Modifier m) => new DraftModifier
        {
            Id                = m.Id,
            DurationTicks     = m.DurationTicks,
            Stacking          = m.Stacking,
            MaxStacks         = m.MaxStacks,
            MaxHealthDelta    = m.MaxHealthDelta,
            AttackDamageDelta = m.AttackDamageDelta,
            MoveSpeedDelta    = m.MoveSpeedDelta,
            Status            = m.Status,
            Period            = m.PeriodEffect is null ? null : DraftNode.FromEffectNode(m.PeriodEffect),
            PeriodTicks       = m.PeriodTicks,
        };
    }

    /// <summary>
    /// A MUTABLE authoring/scratch node — the heart of the structured composer (Story 2.5b). The runtime
    /// <see cref="EffectNode"/> graph is immutable (every subtype is sealed, all-<c>readonly</c>, ctor-only), so the
    /// composer CANNOT edit a node or its children in place (that is the 1.12/1.13 "zombie-state" trap — never
    /// <c>seq.Children[i] = …</c>). Instead the composer mutates this reference-type record (trivial list/field
    /// edits for add/remove/reorder/nest/kind-change — no parent re-threading) and MATERIALIZES a fresh immutable
    /// tree via <see cref="ToEffectNode"/> only at Show-JSON / validate / save time. The mirror is
    /// <see cref="FromEffectNode"/> (parse a loaded/validated graph back into editable form).
    ///
    /// This does NOT violate <c>AbilityDefinition</c>'s Decision #1 ("the runtime graph IS the deserialization
    /// target — no parallel DTO tree"): that governs the LOAD target only; a visual tree editor inherently needs a
    /// mutable authoring model, and this one never touches the load/deserialize path.
    /// </summary>
    public sealed class DraftNode
    {
        public DraftKind Kind;

        // ── Leaf / scalar fields (only the ones relevant to Kind are surfaced by the editor) ──
        public Fixed      Delta;                              // direct_hp_delta
        public Fixed      Amount;                             // heal, damage
        public DamageType DamageType = DamageType.Normal;     // damage
        public Fixed      Radius;                             // search_area
        public TargetFilter Filter   = TargetFilter.Enemy;    // search_area
        public int        PeriodTicks;                        // persistent
        public int        PeriodCount;                        // persistent

        // ── Container slots ──
        public readonly List<DraftNode> Children = new();     // sequence (≥1, ≤8 by the validator/caps)
        public DraftNode? Child;                              // search_area (required)
        public DraftNode? Initial;                            // persistent.initial_effect (optional)
        public DraftNode? Period;                             // persistent.period_effect  (optional)
        public DraftNode? Expire;                             // persistent.expire_effect  (optional)
        public DraftModifier Modifier = new();                // apply_modifier

        /// <summary>A fresh node of <paramref name="kind"/> seeded with sensible authoring defaults.</summary>
        public static DraftNode NewDefault(DraftKind kind)
        {
            var n = new DraftNode();
            n.ResetKind(kind);
            return n;
        }

        /// <summary>
        /// Change this node's kind in place, clearing ALL slots and reseeding defaults — "preserve nothing across an
        /// incompatible kind switch" (Task 1.4), kept predictable. In-place reset is safe (the parent still references
        /// this same node); the immutable-tree rule only bites at materialize time.
        /// </summary>
        public void ResetKind(DraftKind kind)
        {
            Kind = kind;
            Children.Clear();
            Child = Initial = Period = Expire = null;
            Delta = Amount = Radius = Fixed.Zero;
            DamageType = DamageType.Normal;
            Filter = TargetFilter.Enemy;
            PeriodTicks = PeriodCount = 0;
            Modifier = new DraftModifier();

            // A couple of UX-friendly non-zero seeds (authoring defaults only; the validator remains the gate).
            switch (kind)
            {
                case DraftKind.SearchArea: Radius = Fixed.FromInt(4); break;   // a usable default area
                case DraftKind.Persistent: PeriodTicks = 30; PeriodCount = 3; break;
            }
        }

        /// <summary>
        /// Materialize the immutable runtime <see cref="EffectNode"/> via the verified public constructors. Each call
        /// builds a FRESH node (never mutates an existing one). Throws <see cref="InvalidOperationException"/> with a
        /// friendly message for a structurally-incomplete required slot (a <c>SearchArea</c> with no child) — the panel
        /// surfaces it inline rather than constructing a malformed tree.
        /// </summary>
        public EffectNode ToEffectNode()
        {
            switch (Kind)
            {
                case DraftKind.DirectHpDelta:
                    return new DirectHpDeltaEffect(Delta);

                case DraftKind.Heal:
                    return new HealEffect(Amount);

                case DraftKind.Damage:
                    return new DamageEffect(Amount, DamageType);   // ctor order: (amount, type) — verified

                case DraftKind.ApplyModifier:
                    return new ApplyModifierEffect(Modifier.ToModifier());

                case DraftKind.Sequence:
                {
                    var arr = new EffectNode[Children.Count];      // variable size (no magic-cap literal); 0-child stays a validator reject
                    for (int i = 0; i < Children.Count; i++)
                        arr[i] = Children[i].ToEffectNode();
                    return new SequenceEffect(arr);                // params ctor accepts an array
                }

                case DraftKind.SearchArea:
                    if (Child is null)
                        throw new InvalidOperationException("A Search Area needs a child effect — add one before saving.");
                    return new SearchAreaEffect(Radius, Filter, Child.ToEffectNode());

                case DraftKind.Persistent:
                    return new PersistentEffect(
                        Initial?.ToEffectNode(), Period?.ToEffectNode(), Expire?.ToEffectNode(),
                        PeriodTicks, PeriodCount);

                default:
                    throw new InvalidOperationException($"Unknown draft kind '{Kind}'.");
            }
        }

        /// <summary>Build a mutable draft tree from an existing immutable runtime graph (the load / parse-in path).</summary>
        public static DraftNode FromEffectNode(EffectNode node)
        {
            switch (node)
            {
                case DirectHpDeltaEffect e:
                    return new DraftNode { Kind = DraftKind.DirectHpDelta, Delta = e.Delta };

                case HealEffect e:
                    return new DraftNode { Kind = DraftKind.Heal, Amount = e.Amount };

                case DamageEffect e:
                    return new DraftNode { Kind = DraftKind.Damage, Amount = e.Amount, DamageType = e.Type };

                case ApplyModifierEffect e:
                    return new DraftNode { Kind = DraftKind.ApplyModifier, Modifier = DraftModifier.FromModifier(e.Modifier) };

                case SequenceEffect e:
                {
                    var n = new DraftNode { Kind = DraftKind.Sequence };
                    foreach (EffectNode c in e.Children)
                        n.Children.Add(FromEffectNode(c));
                    return n;
                }

                case SearchAreaEffect e:
                    return new DraftNode
                    {
                        Kind = DraftKind.SearchArea, Radius = e.Radius, Filter = e.Filter,
                        Child = FromEffectNode(e.Child),
                    };

                case PersistentEffect e:
                    return new DraftNode
                    {
                        Kind = DraftKind.Persistent, PeriodTicks = e.PeriodTicks, PeriodCount = e.PeriodCount,
                        Initial = e.InitialEffect is null ? null : FromEffectNode(e.InitialEffect),
                        Period  = e.PeriodEffect  is null ? null : FromEffectNode(e.PeriodEffect),
                        Expire  = e.ExpireEffect  is null ? null : FromEffectNode(e.ExpireEffect),
                    };

                default:
                    throw new InvalidOperationException($"Unknown effect node type '{node.GetType().Name}'.");
            }
        }

        // ── Caps metrics (Godot-free, Tier-1-tested) — power the panel's in-UI EffectCaps guardrail (Task 1.5). ──
        // The AbilityValidator remains the authoritative gate on save; these only drive the "grey out the add that
        // would breach a cap" affordances + friendly messaging, so they need only be conservative, not the gate.

        /// <summary>Total node count in this subtree (self + every descendant across all slots, incl. a modifier's period effect).</summary>
        public int CountNodes()
        {
            int n = 1;
            foreach (DraftNode c in Children) n += c.CountNodes();
            if (Child   is not null) n += Child.CountNodes();
            if (Initial is not null) n += Initial.CountNodes();
            if (Period  is not null) n += Period.CountNodes();
            if (Expire  is not null) n += Expire.CountNodes();
            if (Kind == DraftKind.ApplyModifier && Modifier.Period is not null) n += Modifier.Period.CountNodes();
            return n;
        }

        /// <summary>Composition nesting depth of this subtree (self = 1). ApplyModifier is treated as a structural leaf
        /// (mirrors EffectBounds), so a modifier's period effect does not deepen the composition depth.</summary>
        public int Depth()
        {
            int childMax = 0;
            void Consider(DraftNode? c) { if (c is not null) { int d = c.Depth(); if (d > childMax) childMax = d; } }
            foreach (DraftNode c in Children) Consider(c);
            Consider(Child); Consider(Initial); Consider(Period); Consider(Expire);
            return 1 + childMax;
        }

        /// <summary>Max number of <c>SearchArea</c> nodes on any root→leaf path within this subtree (the
        /// MaxSearchAreaDepth metric). Does not descend a modifier's period effect (a SearchArea there is a validator reject).</summary>
        public int SearchAreaDepth()
        {
            int childMax = 0;
            void Consider(DraftNode? c) { if (c is not null) { int d = c.SearchAreaDepth(); if (d > childMax) childMax = d; } }
            foreach (DraftNode c in Children) Consider(c);
            Consider(Child); Consider(Initial); Consider(Period); Consider(Expire);
            return (Kind == DraftKind.SearchArea ? 1 : 0) + childMax;
        }
    }

    /// <summary>
    /// A MUTABLE authoring/scratch model for a whole <see cref="AbilityDefinition"/> — what the Ability Editor's
    /// structured Advanced composer owns and edits (Story 2.5b). Holds the header (id/name/targeting), the costs +
    /// cooldown, and the effect TREE (<see cref="DraftNode"/>). <see cref="ToDefinition"/> materializes the immutable
    /// <see cref="AbilityDefinition"/> (header + costs + the freshly-built effect graph) for serialize/validate/save;
    /// <see cref="FromDefinition"/> seeds the draft from a parsed/validated ability (the single shared load path).
    /// Pure C# / <c>Fixed</c>-only — no Godot, no float (the SpinBox double↔Fixed boundary lives in the panel).
    /// </summary>
    public sealed class AbilityDraft
    {
        public string Id          = "";
        public string DisplayName = "";
        public string Targeting   = "Self";
        public Fixed  CostEnergy  = Fixed.Zero;
        public int    CostOre;
        public int    CostCrystal;
        public Fixed  Cooldown    = Fixed.Zero;
        public DraftNode? Effect;

        /// <summary>Materialize the immutable <see cref="AbilityDefinition"/> (header + costs + the built effect graph).
        /// May throw <see cref="InvalidOperationException"/> via <see cref="DraftNode.ToEffectNode"/> on an incomplete
        /// required slot — the panel catches it and surfaces a friendly inline error.</summary>
        public AbilityDefinition ToDefinition() => new AbilityDefinition
        {
            Id          = Id,
            DisplayName = DisplayName,
            Targeting   = Targeting,
            CostEnergy  = CostEnergy,
            CostOre     = CostOre,
            CostCrystal = CostCrystal,
            Cooldown    = Cooldown,
            EffectGraph = Effect?.ToEffectNode(),
        };

        /// <summary>Seed a mutable draft from a parsed/validated ability (the load / round-trip-in path).</summary>
        public static AbilityDraft FromDefinition(AbilityDefinition def) => new AbilityDraft
        {
            Id          = def.Id,
            DisplayName = def.DisplayName,
            Targeting   = def.Targeting,
            CostEnergy  = def.CostEnergy,
            CostOre     = def.CostOre,
            CostCrystal = def.CostCrystal,
            Cooldown    = def.Cooldown,
            Effect      = def.EffectGraph is null ? null : DraftNode.FromEffectNode(def.EffectGraph),
        };
    }
}
