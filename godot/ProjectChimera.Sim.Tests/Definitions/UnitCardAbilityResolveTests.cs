#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.3 (AC1 / D-3) — <see cref="UnitCardText.ResolveAbilityLabels"/>: id → display-name resolution for the
    /// card's attached-ability list. Known id → <see cref="AbilityDefinition.DisplayName"/>; unknown id / the
    /// <see cref="AbilityRegistry.Empty"/> registry → raw-id fallback (never a blank row); ALL ids included in list
    /// order — passives too, because the resolver reads the raw <see cref="UnitDefinition.Abilities"/> array, NOT the
    /// castable-only <c>[JsonIgnore] AbilityIndices</c> (which partitions passives out). Godot-free.
    /// </summary>
    public class UnitCardAbilityResolveTests
    {
        /// <summary>Build a stub registry from (id, display-name, activation) triples — no validation, no effect graph.</summary>
        private static AbilityRegistry Registry(params (string Id, string Name, string Activation)[] abilities)
        {
            var defs = new List<AbilityDefinition>();
            foreach (var (id, name, activation) in abilities)
                defs.Add(new AbilityDefinition { Id = id, DisplayName = name, Activation = activation });
            return new AbilityRegistry(defs);
        }

        [Fact]
        public void KnownId_ResolvesToDisplayName()
        {
            AbilityRegistry reg = Registry(("fireball", "Fireball", "active"));
            Assert.Equal(new[] { "Fireball" }, UnitCardText.ResolveAbilityLabels(new[] { "fireball" }, reg));
        }

        [Fact]
        public void UnknownId_FallsBackToRawId()
        {
            AbilityRegistry reg = Registry(("fireball", "Fireball", "active"));
            Assert.Equal(new[] { "mystery_spell" }, UnitCardText.ResolveAbilityLabels(new[] { "mystery_spell" }, reg));
        }

        [Fact]
        public void EmptyRegistry_FallsBackToRawIds()
        {
            Assert.Equal(new[] { "a", "b" }, UnitCardText.ResolveAbilityLabels(new[] { "a", "b" }, AbilityRegistry.Empty));
        }

        [Fact]
        public void PassiveIds_AreIncluded_NotDroppedLikeAbilityIndices()
        {
            // An aura (passive) + an active ability: BOTH resolve, in list order. UnitDefinition.AbilityIndices would
            // drop the aura; the card's resolver never partitions by activation, so the passive still shows.
            AbilityRegistry reg = Registry(
                ("iron_aura", "Iron Aura", "aura"),
                ("fireball", "Fireball", "active"));
            Assert.Equal(
                new[] { "Iron Aura", "Fireball" },
                UnitCardText.ResolveAbilityLabels(new[] { "iron_aura", "fireball" }, reg));
        }

        [Fact]
        public void EmptyInput_YieldsEmpty()
        {
            AbilityRegistry reg = Registry(("fireball", "Fireball", "active"));
            Assert.Empty(UnitCardText.ResolveAbilityLabels(System.Array.Empty<string>(), reg));
        }

        [Fact]
        public void NullInput_YieldsEmpty()
        {
            Assert.Empty(UnitCardText.ResolveAbilityLabels(null, AbilityRegistry.Empty));
        }

        [Fact]
        public void ResolvedButUnnamedAbility_FallsBackToRawId()
        {
            // A registry ability with an empty DisplayName must not render a blank row — fall back to the id.
            AbilityRegistry reg = Registry(("silent", "", "active"));
            Assert.Equal(new[] { "silent" }, UnitCardText.ResolveAbilityLabels(new[] { "silent" }, reg));
        }

        [Fact]
        public void PreservesOrderAndDuplicates()
        {
            AbilityRegistry reg = Registry(("a", "Alpha", "active"), ("b", "Beta", "active"));
            Assert.Equal(
                new[] { "Beta", "Alpha", "Beta" },
                UnitCardText.ResolveAbilityLabels(new[] { "b", "a", "b" }, reg));
        }
    }
}
