#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// Story 15.11 (DW-286/DW-290, review P5) — the Godot-free cast-target picker cores extracted from the
    /// Godot-coupled SelectionSystem/CommandCardSystem. Covers the affinity click-pick rules (ally EXCLUDES the caster,
    /// any excludes only the caster, enemy = non-local non-Neutral, radius bound) and the single is-castable predicate
    /// that gates BOTH the command card's button-disable and its press-router.
    /// </summary>
    public class CastTargetPickerTests
    {
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        [Fact]
        public void Ally_PicksOwnFaction_ExcludingTheCaster()
        {
            var w = new EntityWorld();
            int caster = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int ally   = w.Create(V(2, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.Create(V(1, 0), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // an enemy, closer than the ally

            // Hit right on the caster: the caster is the nearest own-faction unit, but Ally EXCLUDES it (heal-OTHER),
            // and the enemy is not own-faction — so the ally is picked.
            int pick = CastTargetPicker.FindNearest(w, 0f, 0f, 50f, Faction.Player1, caster, TargetAffinity.Ally);
            Assert.Equal(ally, pick);
        }

        [Fact]
        public void Ally_ReturnsMinusOne_WhenOnlyTheCasterIsInRange()
        {
            var w = new EntityWorld();
            int caster = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.Create(V(40, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // ally outside the radius

            int pick = CastTargetPicker.FindNearest(w, 0f, 0f, 5f, Faction.Player1, caster, TargetAffinity.Ally);
            Assert.Equal(-1, pick);
        }

        [Fact]
        public void Any_ExcludesOnlyTheCaster()
        {
            var w = new EntityWorld();
            int caster = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int enemy  = w.Create(V(2, 0), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            w.Create(V(5, 0), Faction.Neutral, Fixed.FromInt(100), Fixed.FromInt(3));

            // Nearest non-caster of ANY allegiance → the enemy (closer than the neutral; the caster is excluded).
            int pick = CastTargetPicker.FindNearest(w, 0f, 0f, 50f, Faction.Player1, caster, TargetAffinity.Any);
            Assert.Equal(enemy, pick);
        }

        [Fact]
        public void Enemy_SkipsNeutralAndCaster_PicksHostileFaction()
        {
            var w = new EntityWorld();
            w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));  // the local caster (id 0)
            w.Create(V(1, 0), Faction.Neutral, Fixed.FromInt(100), Fixed.FromInt(3));  // closest, but NOT an enemy
            int enemy = w.Create(V(3, 0), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));

            // Enemy = not the local faction and not Neutral; casterId -1 (no exclusion) is the SelectionSystem contract.
            int pick = CastTargetPicker.FindNearest(w, 0f, 0f, 50f, Faction.Player1, -1, TargetAffinity.Enemy);
            Assert.Equal(enemy, pick);
        }

        // ── The DW-290 shared predicate — the SAME value gates both the card disable-gate and the press-router
        //    (CommandCardSystem.IsTargetingCastable forwards to this, so both call sites resolve identically). ──

        [Theory]
        [InlineData(AbilityTargeting.Self)]
        [InlineData(AbilityTargeting.None)]
        [InlineData(AbilityTargeting.TargetUnit)]
        [InlineData(AbilityTargeting.GroundPoint)]
        public void IsTargetingCastable_TrueForEveryKnownMode(AbilityTargeting targeting)
            => Assert.True(CastTargetPicker.IsTargetingCastable(targeting));

        [Fact]
        public void IsTargetingCastable_FalseForUnknownTargeting()
            => Assert.False(CastTargetPicker.IsTargetingCastable(null)); // an unparseable targeting string → not castable
    }
}
