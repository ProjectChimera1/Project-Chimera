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

        /// <summary>
        /// DW-903 — a leaf the structured composer cannot EDIT but must not LOSE: it carries the original immutable
        /// node through unchanged. Deliberately absent from <see cref="DraftVocabulary.Kinds"/>, so the composer never
        /// OFFERS it — a draft node of this kind only ever arrives by loading existing content.
        ///
        /// <para>Story 15.13 added four leaves to the closed vocabulary (teleport, play_vfx, play_sound, shake_screen)
        /// and did not teach the composer about them. <see cref="DraftNode.FromEffectNode"/>'s default arm THROWS, and
        /// nothing on the <c>LoadFromRegistry → ReflectModelIntoForm → SeedDraftFromDef</c> path catches it — so
        /// opening any ability that uses one (the shipped <c>blink_strike</c> does) took the editor down. Passing the
        /// node through opaquely fixes the crash AND is lossless, which a "skip what I don't understand" arm would not
        /// be. Rich authoring widgets for these four are the follow-up.</para>
        /// </summary>
        Opaque        = 7,
    }

    /// <summary>
    /// The closed sets of values the structured composer is allowed to OFFER on its new dropdowns (AC5-COMPOSER).
    /// Hosted Godot-free so the closed-vocabulary guarantee is Tier-1-testable (and the panel builds its dropdowns
    /// from these — one source of truth, no parallel list to drift): the <c>damage_type</c> set excludes the
    /// internal sentinel <see cref="DamageType.COUNT"/>; the <c>filter</c> set now INCLUDES the
    /// <see cref="TargetFilter.Air"/>/<see cref="TargetFilter.Ground"/>/<see cref="TargetFilter.Structure"/> domain bits
    /// (Story 2.9a — evaluated by <see cref="TargetMatcher"/>). Because the composer can only ever CONSTRUCT a node from these, it can never build a node the
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

        /// <summary>Authorable target filters — allegiance + Alive + the Air/Ground/Structure domain bits (Story 2.9a, now evaluated).</summary>
        public static readonly TargetFilter[] Filters =
        {
            TargetFilter.None, TargetFilter.Self, TargetFilter.Ally, TargetFilter.Enemy,
            TargetFilter.Neutral, TargetFilter.Alive,
            TargetFilter.Air, TargetFilter.Ground, TargetFilter.Structure,
        };

        /// <summary>Authorable stacking rules (the full closed <see cref="StackRule"/> set; DW-264 adds StackIndependent).</summary>
        public static readonly StackRule[] StackRules =
            { StackRule.Refresh, StackRule.Stack, StackRule.Ignore, StackRule.StackIndependent };

        /// <summary>Authorable periodic-stacking modes (the full closed <see cref="PeriodicStackMode"/> set; DW-272).</summary>
        public static readonly PeriodicStackMode[] PeriodicStackModes =
            { PeriodicStackMode.None, PeriodicStackMode.Multiply, PeriodicStackMode.Repeat };

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
        public int         DurationTicks;                          // <0 = permanent, 0 = ONE TICK (not instantaneous — DW-270)
        public StackRule   Stacking         = StackRule.Refresh;   // converter read-fallback
        public int         MaxStacks        = 1;                   // converter read-fallback
        public Fixed       MaxHealthDelta   = Fixed.Zero;
        public Fixed       AttackDamageDelta = Fixed.Zero;
        public Fixed       MoveSpeedDelta   = Fixed.Zero;
        public Fixed       ArmorDelta       = Fixed.Zero;          // Story 2.6 — flat armor buff (e.g. aura grant)
        public StatusFlags Status           = StatusFlags.None;
        public DraftNode?  Period;                                 // optional period_effect (DoT/HoT)
        public int         PeriodTicks;
        public PeriodicStackMode PeriodicStacking = PeriodicStackMode.None; // DW-272 — how a stacked pulse scales

        /// <summary>Materialize the immutable runtime <see cref="Modifier"/> (constructor order is load-bearing — verified).</summary>
        public Modifier ToModifier() => new Modifier(
            Id, DurationTicks, Stacking, MaxStacks,
            MaxHealthDelta, AttackDamageDelta, MoveSpeedDelta,
            Status, Period?.ToEffectNode(), PeriodTicks, ArmorDelta, PeriodicStacking);

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
            ArmorDelta        = m.ArmorDelta,
            Status            = m.Status,
            Period            = m.PeriodEffect is null ? null : DraftNode.FromEffectNode(m.PeriodEffect),
            PeriodTicks       = m.PeriodTicks,
            PeriodicStacking  = m.PeriodicStacking,
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

        /// <summary>
        /// DW-323 — <c>persistent.lifelong</c> (Story 2.13): the flag that RE-ARMS the periodic pulse budget so a
        /// permanent HoT/DoT keeps pulsing past the <c>EffectCaps.MaxPersistentPeriods</c> window instead of expiring
        /// at it.
        /// <para>Its absence here was silent DATA LOSS, not a missing feature: <see cref="ToEffectNode"/> built its
        /// <see cref="PersistentEffect"/> with <c>lifelong</c> defaulted to false and <see cref="FromEffectNode"/>
        /// never captured it, so opening a lifelong ability (the shipped <c>furnace_trickle</c> /
        /// <c>furnace_pour</c>) in the Advanced composer and saving STRIPPED the flag — re-introducing the 256-pulse
        /// defect Story 2.13 fixed, invisibly. The validator could not catch it either: it only rejects a lifelong
        /// WITHOUT a period, never a period that lost its lifelong.</para>
        /// </summary>
        public bool       Lifelong;                           // persistent

        // ── Container slots ──
        public readonly List<DraftNode> Children = new();     // sequence (≥1, ≤8 by the validator/caps)
        public DraftNode? Child;                              // search_area (required)
        public DraftNode? Initial;                            // persistent.initial_effect (optional)
        public DraftNode? Period;                             // persistent.period_effect  (optional)
        public DraftNode? Expire;                             // persistent.expire_effect  (optional)
        public DraftModifier Modifier = new();                // apply_modifier

        /// <summary>DW-903: the untouched original node for <see cref="DraftKind.Opaque"/> — carried through
        /// materialize unchanged so a composer round-trip cannot silently drop a leaf it cannot render.</summary>
        public EffectNode? Opaque;

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
            Opaque = null;
            Delta = Amount = Radius = Fixed.Zero;
            DamageType = DamageType.Normal;
            Filter = TargetFilter.Enemy;
            PeriodTicks = PeriodCount = 0;
            Lifelong = false;                                 // DW-323 — cleared with the rest of the persistent slots
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
                        PeriodTicks, PeriodCount, Lifelong);   // DW-323 — lifelong is carried, not defaulted away

                case DraftKind.Opaque:
                    // DW-903: hand back the very node we were loaded from. EffectNodes are immutable, so sharing the
                    // instance is safe and is what makes the round-trip byte-identical.
                    return Opaque ?? throw new InvalidOperationException(
                        "This effect was loaded from existing content and cannot be rebuilt — edit it in the Raw JSON pane.");

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
                        Lifelong = e.Lifelong,                 // DW-323 — the inverse of ToEffectNode (round-trips unchanged)
                        Initial = e.InitialEffect is null ? null : FromEffectNode(e.InitialEffect),
                        Period  = e.PeriodEffect  is null ? null : FromEffectNode(e.PeriodEffect),
                        Expire  = e.ExpireEffect  is null ? null : FromEffectNode(e.ExpireEffect),
                    };

                // DW-903 — Story 15.13's four leaves. The composer has no widgets for a teleport's (empty) shape or a
                // presentation cue's CombatFeedbackProfile, so they load OPAQUELY: shown read-only, materialized back
                // out untouched. Listed EXPLICITLY rather than folded into the default arm on purpose — a genuinely
                // unknown node type must still throw loudly, because that means the vocabulary grew and nobody told
                // this file.
                case TeleportEffect:
                case PlayVfxEffect:
                case PlaySoundEffect:
                case ShakeScreenEffect:
                    return new DraftNode { Kind = DraftKind.Opaque, Opaque = node };

                default:
                    throw new InvalidOperationException($"Unknown effect node type '{node.GetType().Name}'.");
            }
        }

        // ── Caps metric (Godot-free, Tier-1-tested) — powers the panel's in-UI EffectCaps guardrail (Task 1.5). ──
        // The AbilityValidator remains the authoritative gate on save; this only drives the "grey out the add that
        // would breach a cap" affordance + friendly messaging, so it need only be conservative, not the gate.
        //
        // DW-297 — there is deliberately only ONE metric here. Two sibling metrics (`Depth()` and
        // `SearchAreaDepth()`) once sat alongside it and were referenced by nothing but their own unit test, because
        // the two DEPTH caps cannot be served by a bottom-up subtree measurement at all:
        //   • The panel's question is "which kinds may I add IN THIS SLOT", which depends on the depth ABOVE the slot,
        //     not below it. AbilityEditorPanel.Advanced answers it with the top-down `TreeCtx` it already threads
        //     through the render walk (CompDepth + SearchAncestors), which is the only context a freshly-added leaf has.
        //   • Worse, the deleted `Depth()` did not even agree with the cap it named: it counted EVERY node on a path
        //     (self = 1, terminal leaves included), whereas EffectCaps.MaxEffectDepth counts only COMPOSITION nodes
        //     (EffectBounds: root frame = 0, leaves contribute nothing). A legal chain of MaxEffectDepth compositions
        //     ending in a leaf measured MaxEffectDepth + 1, so any `Depth() <= MaxEffectDepth` guard built on it would
        //     have rejected content the loader accepts. An unused metric cannot be trusted; this one is used and pinned.
        // Any depth metric re-added here MUST be pinned against EffectBounds/AbilityValidator, not merely unit-tested
        // against hand-counted literals — see AbilityDraftTests.

        /// <summary>
        /// Total node count in this subtree (self + every descendant across all slots, INCLUDING an
        /// <c>ApplyModifier</c>'s period effect). Deliberately mirrors <c>AbilityValidator.WalkGraph</c>'s
        /// <see cref="EffectCaps.MaxTotalEffectNodes"/> tally — which also descends a modifier's period subtree — so the
        /// panel's budget and the save-time gate agree on what "64 nodes" means. Pinned at the cap boundary by
        /// <c>AbilityDraftTests</c>.
        /// </summary>
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
        public string? TargetAffinity = null;   // Story 15.11 (DW-286) — optional Enemy|Ally|Any; null = absent (enemy default), round-trips unchanged
        public string Activation  = "active";   // Story 2.6 — active | aura | on_hit | while_alive (closed set)
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
            TargetAffinity = TargetAffinity, // Story 15.11 (null → absent, serializes identically to today)
            Activation  = Activation,
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
            TargetAffinity = def.TargetAffinity, // Story 15.11 — inverse of ToDefinition (round-trips null unchanged)
            Activation  = def.Activation,
            CostEnergy  = def.CostEnergy,
            CostOre     = def.CostOre,
            CostCrystal = def.CostCrystal,
            Cooldown    = def.Cooldown,
            Effect      = def.EffectGraph is null ? null : DraftNode.FromEffectNode(def.EffectGraph),
        };
    }
}
