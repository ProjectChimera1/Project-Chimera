#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The closed set of Simple-mode role bundles for the Unit Card Editor (Story 3.6, mirroring
    /// <see cref="AbilityPresets"/>). Each bundle is a pure <c>Kind → ability-id set</c> mapping: the Simple form offers
    /// them as a "Composition" dropdown so a creator composes a unit's role WITHOUT hand-picking abilities. Godot-free,
    /// deterministic, Tier-1-testable.
    ///
    /// <para><b>Lenient application (D-3).</b> Applying a preset REPLACES <see cref="UnitDefinition.Abilities"/> with the
    /// bundle's ids; the panel drops any id absent from the live <see cref="AbilityRegistry"/> (the same lenient posture as
    /// <see cref="UnitDefinition.ResolveAbilities"/>), so a bundle referencing an ability a project hasn't shipped simply
    /// contributes fewer ids — never an invalid ref. <see cref="Detect"/> is the inverse (id-set equality) so the dropdown
    /// round-trips losslessly: an arbitrary/hand-edited ability set that matches no bundle reads back as <see cref="Kind.Custom"/>.</para>
    /// </summary>
    public static class UnitCompositionPresets
    {
        /// <summary>The closed composition registry (display order = dropdown order). <see cref="Custom"/> = "no bundle" —
        /// the fallback for any ability set no other bundle matches (incl. the empty set).</summary>
        public enum Kind : byte
        {
            /// <summary>No preset — the ability set was hand-composed (or is empty). Selecting it makes no change.</summary>
            Custom = 0,
            /// <summary>A support caster: a single-target heal.</summary>
            Healer = 1,
            /// <summary>A frontline melee bruiser: a timed self attack-damage buff.</summary>
            Bruiser = 2,
            /// <summary>A ranged nuker: a single-target burst spell.</summary>
            Caster = 3,
        }

        /// <summary>The closed preset list for the Simple-mode dropdown (stable order).</summary>
        public static readonly (Kind Kind, string Label)[] All =
        {
            (Kind.Custom,  "Custom"),
            (Kind.Healer,  "Healer"),
            (Kind.Bruiser, "Bruiser"),
            (Kind.Caster,  "Caster"),
        };

        // The ability ids each bundle composes. These reference abilities that ship under resources/data/abilities/;
        // an id absent from the live AbilityRegistry is dropped when the preset is applied (lenient — D-3).
        private static readonly string[] _healer  = { "minor_heal" };
        private static readonly string[] _bruiser = { "battle_fury" };
        private static readonly string[] _caster  = { "fireball" };

        /// <summary>The ability ids for <paramref name="kind"/> (a fresh array; <see cref="Kind.Custom"/> ⇒ empty).</summary>
        public static string[] Bundle(Kind kind) => kind switch
        {
            Kind.Healer  => (string[])_healer.Clone(),
            Kind.Bruiser => (string[])_bruiser.Clone(),
            Kind.Caster  => (string[])_caster.Clone(),
            Kind.Custom  => Array.Empty<string>(),
            _            => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown composition preset kind."),
        };

        /// <summary>
        /// The inverse of <see cref="Bundle"/>: the <see cref="Kind"/> whose bundle is an exact id-SET match for
        /// <paramref name="abilities"/> (order-independent, duplicate-insensitive), or <see cref="Kind.Custom"/> when
        /// none match. Guarantees a lossless dropdown round-trip — a preset-shaped set reads back to its <see cref="Kind"/>,
        /// and any other set (incl. empty) reads back as <see cref="Custom"/> so applying the dropdown never silently
        /// rewrites a hand-composed unit.
        /// </summary>
        public static Kind Detect(string[]? abilities)
        {
            var have = new HashSet<string>(abilities ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach ((Kind kind, _) in All)
            {
                if (kind == Kind.Custom) continue;
                var bundle = new HashSet<string>(Bundle(kind), StringComparer.Ordinal);
                if (have.SetEquals(bundle)) return kind;
            }
            return Kind.Custom;
        }
    }
}
