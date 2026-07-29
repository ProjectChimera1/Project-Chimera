#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Definitions; // AbilityRegistry, AbilityDefinition, ItemRegistry, ItemDefinition
using ProjectChimera.Effects;          // EffectNode + subclasses, Modifier, PersistentEffect

namespace ProjectChimera.Core.Persistence
{
    /// <summary>
    /// Story 11.3 (FR-67) — the deterministic, content-driven lookup that makes <see cref="ModifierStore"/> slots
    /// serializable by index. A live modifier/persistent slot holds an object REFERENCE to a
    /// <see cref="Modifier"/> / <see cref="PersistentEffect"/> descriptor that carries no stable id, so a save cannot
    /// persist it directly. This table walks ALL loaded modifier/persistent-granting content — the ability registry
    /// then the item registry, each already Id-sorted, in a FIXED effect-graph traversal order — and assigns every
    /// distinct descriptor a stable index. The serializer stores that index per active slot; load re-resolves the
    /// descriptor from the table.
    ///
    /// <para><b>Why the index is stable across a save.</b> The table is derived PURELY from content that is
    /// byte-identical across the save (the save header's <c>ContentHash</c> fail-closes any drift), and the walk is
    /// a deterministic recursion in a fixed child order, so the same content yields the same descriptor objects in
    /// the same order every time — a stable serialization key (the Story 11.3 "hard sub-problem" resolution). If a
    /// content path can grant a descriptor UNREACHABLE by this walk, that is the
    /// <c>modifier descriptor round-trip needs a content-model change</c> Block-If, surfaced as
    /// <see cref="IndexOfModifier"/>/<see cref="IndexOfPersistent"/> returning −1 at capture time.</para>
    ///
    /// <para>Godot-free, float-free, pure sim (Tier-1). The reference-identity dictionaries are used for O(1)
    /// descriptor→index LOOKUP only — never enumerated (the determinism/analyzer contract; the ordered lists are the
    /// enumeration surface).</para>
    /// </summary>
    public sealed class CanonicalEffectDescriptorTable
    {
        private readonly List<Modifier> _modifiers = new();
        private readonly List<PersistentEffect> _persistents = new();
        private readonly Dictionary<Modifier, int> _modifierIndex = new();          // reference identity (Modifier has no equality override)
        private readonly Dictionary<PersistentEffect, int> _persistentIndex = new(); // reference identity

        /// <summary>Distinct <see cref="Modifier"/> descriptors reachable from loaded content.</summary>
        public int ModifierCount => _modifiers.Count;

        /// <summary>Distinct <see cref="PersistentEffect"/> descriptors reachable from loaded content.</summary>
        public int PersistentCount => _persistents.Count;

        /// <summary>
        /// Build the table by walking the ability registry then the item registry (each Id-sorted). A null registry
        /// contributes nothing. Deterministic: registry order is fixed and the effect-graph walk is a fixed-order
        /// recursion, so two identical content sets produce identical indices.
        /// </summary>
        public static CanonicalEffectDescriptorTable Build(AbilityRegistry? abilities, ItemRegistry? items)
        {
            var t = new CanonicalEffectDescriptorTable();
            if (abilities != null)
                for (int i = 0; i < abilities.Count; i++)
                    t.Walk(abilities.Get(i).EffectGraph);
            if (items != null)
                for (int i = 0; i < items.Count; i++)
                    t.Walk(items.Get(i).EffectGraph);
            return t;
        }

        // Deterministic effect-graph recursion in a fixed child order — mirrors the engine's own EffectBounds walk.
        private void Walk(EffectNode? node)
        {
            switch (node)
            {
                case null:
                    return;
                case ApplyModifierEffect a:
                    AddModifier(a.Modifier);
                    Walk(a.Modifier.PeriodEffect); // a Modifier's period pulse may itself grant further descriptors
                    return;
                case PersistentEffect p:
                    AddPersistent(p);
                    Walk(p.InitialEffect);
                    Walk(p.PeriodEffect);
                    Walk(p.ExpireEffect);
                    return;
                case SequenceEffect s:
                    for (int i = 0; i < s.Children.Length; i++) Walk(s.Children[i]);
                    return;
                case SearchAreaEffect sa:
                    Walk(sa.Child);
                    return;
                default:
                    return; // DamageEffect / HealEffect / DirectHpDeltaEffect — terminal leaves, no descriptor
            }
        }

        private void AddModifier(Modifier m)
        {
            if (m == null || _modifierIndex.ContainsKey(m)) return;
            _modifierIndex[m] = _modifiers.Count;
            _modifiers.Add(m);
        }

        private void AddPersistent(PersistentEffect p)
        {
            if (p == null || _persistentIndex.ContainsKey(p)) return;
            _persistentIndex[p] = _persistents.Count;
            _persistents.Add(p);
        }

        /// <summary>The stable index of <paramref name="m"/>, or −1 when it is unreachable by the content walk
        /// (the descriptor-round-trip Block-If — the caller fail-closes the save).</summary>
        public int IndexOfModifier(Modifier m) => _modifierIndex.TryGetValue(m, out int i) ? i : -1;

        /// <summary>The stable index of <paramref name="p"/>, or −1 when unreachable (Block-If).</summary>
        public int IndexOfPersistent(PersistentEffect p) => _persistentIndex.TryGetValue(p, out int i) ? i : -1;

        /// <summary>Resolve a serialized modifier index back to its descriptor, or null when out of range (a
        /// content-drift the header's ContentHash already fail-closes; the loader treats null as a corrupt save).</summary>
        public Modifier? GetModifier(int index) => (uint)index < (uint)_modifiers.Count ? _modifiers[index] : null;

        /// <summary>Resolve a serialized persistent index back to its descriptor, or null when out of range.</summary>
        public PersistentEffect? GetPersistent(int index) => (uint)index < (uint)_persistents.Count ? _persistents[index] : null;
    }
}
