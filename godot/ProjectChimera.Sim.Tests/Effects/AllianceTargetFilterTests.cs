#nullable enable
using System.Linq;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // AbilityDefinition, AbilityRegistry, FactionDefinition, ScenarioData
using ProjectChimera.Core.Sim;         // SimulationHost, NullLogSink
using ProjectChimera.Effects;
using ProjectChimera.Multiplayer;      // OrderApplier, UnitOrder
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 9.14 — ability targeting is TEAM-aware: an <c>AllianceStore</c> threaded onto the <see cref="EffectContext"/>
    /// makes a SearchArea's <see cref="TargetFilter.Ally"/> match same-team factions and <see cref="TargetFilter.Enemy"/>
    /// exclude them. Exercised through the SAME production fan-out (<see cref="SearchAreaEffect.FindTargets"/> →
    /// <c>TargetMatcher</c>) the live cast path uses — the only place a NON-null mask reaches the matcher. With a null
    /// mask (bare tests / FFA) the filter reduces to strict faction equality, byte-identical to pre-9.14. Neutral is
    /// never allied to a player, so the Neutral handling is unchanged. Godot-free, integer-only.
    /// </summary>
    public class AllianceTargetFilterTests
    {
        // P1 (caster) allied with P2 (shared team id 1); P3 is its own team (enemy). Neutral is never allied.
        private static AllianceStore P1P2Allied()
        {
            var a = new AllianceStore();
            a.TeamId[(int)Faction.Player2] = (int)Faction.Player1;
            return a;
        }

        private static EffectContext Setup(AllianceStore? mask, out EntityWorld w,
                                           out int ally, out int enemy, out int neutral)
        {
            w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            ally    = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // allied under the mask
            enemy   = w.Create(FixedVec3.Zero, Faction.Player3, Fixed.FromInt(100), Fixed.FromInt(3)); // never allied
            neutral = w.Create(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(100), Fixed.FromInt(3)); // never allied to a player
            var sh = new SpatialHash();
            sh.Rebuild(w);
            return new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh, alliances: mask);
        }

        private static int[] Hits(TargetFilter filter, in EffectContext ctx)
        {
            var probe = new int[EffectCaps.MaxHitsPerSearch];
            var search = new SearchAreaEffect(Fixed.FromInt(10), filter, new DirectHpDeltaEffect(Fixed.FromInt(-1)));
            int n = search.FindTargets(in ctx, probe);
            return probe.Take(n).ToArray();
        }

        [Fact]
        public void Ally_WithMask_MatchesAlliedFaction_ExcludesEnemyAndNeutralAndSelf()
        {
            EffectContext ctx = Setup(P1P2Allied(), out _, out int ally, out int enemy, out int neutral);
            int[] hits = Hits(TargetFilter.Ally, in ctx);
            Assert.Equal(new[] { ally }, hits); // EXACTLY the allied faction — RED if TargetMatcher ignores the mask
            Assert.DoesNotContain(enemy, hits);
            Assert.DoesNotContain(neutral, hits);
        }

        [Fact]
        public void Enemy_WithMask_ExcludesAlliedFaction_KeepsRealEnemy()
        {
            EffectContext ctx = Setup(P1P2Allied(), out _, out int ally, out int enemy, out int neutral);
            int[] hits = Hits(TargetFilter.Enemy, in ctx);
            Assert.Contains(enemy, hits);          // a real enemy still matches
            Assert.DoesNotContain(ally, hits);     // the allied faction is now excluded from Enemy
            Assert.DoesNotContain(neutral, hits);  // Neutral stays out of Enemy (unchanged)
        }

        [Fact]
        public void NullMask_ReducesToStrictFactionEquality_ByteIdenticalToPre914()
        {
            // FFA / bare path: no distinct factions are allied. Ally matches only the caster's own faction (none of the
            // distinct-faction candidates), and Enemy matches every non-self, non-Neutral faction — exactly pre-9.14.
            EffectContext ctx = Setup(null, out _, out int ally, out int enemy, out int neutral);

            int[] allies = Hits(TargetFilter.Ally, in ctx);
            Assert.Empty(allies); // P2/P3 are distinct factions → not allies without a mask

            int[] enemies = Hits(TargetFilter.Enemy, in ctx);
            Assert.Contains(ally, enemies);       // without a mask, P2 reads as an enemy (the behavior the mask changes)
            Assert.Contains(enemy, enemies);
            Assert.DoesNotContain(neutral, enemies);
        }

        // ── SimulationHost wiring: the LIVE host must thread host.Alliances into its AbilityCastSystem, so a cast whose
        //    graph is an Enemy-filtered SearchArea excludes an ALLIED faction. The unit tests above hand-build the
        //    EffectContext with an explicit mask; this drives a real cast through host.StepOnce() so a dropped Alliances
        //    arg on the host's AbilityCastSystem construction (which would null the mask → the ally reads as an enemy)
        //    is caught. Mirrors the combat/fog host-wiring guards; the follow-up review found the cast path unguarded. ──

        /// <summary>A TargetUnit fireball-shape whose graph fans out to ENEMIES in an area — the only cast form that
        /// consults the alliance mask (via the SearchArea TargetFilter). Deal −30 to each Enemy-filtered hit.</summary>
        private static AbilityDefinition EnemyAoeFireball() => new AbilityDefinition
        {
            Id = "team_fireball", DisplayName = "Team Fireball", Targeting = "TargetUnit",
            CostEnergy = Fixed.Zero, Cooldown = Fixed.FromInt(1),
            EffectGraph = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy,
                                               new DirectHpDeltaEffect(Fixed.FromInt(-30))),
        };

        [Fact]
        public void SimulationHost_WiresAllianceIntoLiveAbilityTargeting_AllyExcludedFromEnemyAoe()
        {
            var registry = new AbilityRegistry(new[] { EnemyAoeFireball() });
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(4),
                                             new FactionDefinition(), new FactionDefinition(), registry: registry);
            // Seed a 2v2 mask through the seeder: {P1,P2} allied, {P3,P4} the other team.
            AllianceSeeder.Seed(host.Alliances, new ScenarioData
            {
                PlayerSlots = new[]
                {
                    new ScenarioPlayerSlot { Slot = 0, Team = 1 },
                    new ScenarioPlayerSlot { Slot = 1, Team = 1 },
                    new ScenarioPlayerSlot { Slot = 2, Team = 2 },
                    new ScenarioPlayerSlot { Slot = 3, Team = 2 },
                },
            });
            Assert.True(host.Alliances.AreAllied(Faction.Player1, Faction.Player2));

            var w = host.World;
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AbilityId[caster * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = registry.IndexOf("team_fireball");
            w.AbilityCount[caster] = 1;
            w.MaxEnergy[caster] = Fixed.FromInt(100);
            w.Energy[caster]    = Fixed.FromInt(100);

            // All co-located so the Enemy-filtered SearchArea (centered on the TargetUnit) covers every candidate.
            int primary = w.Create(FixedVec3.Zero, Faction.Player3, Fixed.FromInt(100), Fixed.FromInt(3)); // enemy target
            int ally    = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // allied → excluded
            int enemy   = w.Create(FixedVec3.Zero, Faction.Player4, Fixed.FromInt(100), Fixed.FromInt(3)); // enemy → hit

            Fixed allyHp0 = w.Health[ally];
            // TargetUnit cast onto the enemy primary (slot 0). The graph fans out to Enemy-filtered hits in radius.
            OrderApplier.Apply(w, new UnitOrder(caster, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(primary)),
                               Faction.Player1);
            host.StepOnce(); // the full host pipeline (the wired AbilityCastSystem, not a hand-built one)

            Assert.Equal(allyHp0, w.Health[ally]);               // ally excluded → host DID thread Alliances into ability targeting
            Assert.True(w.Health[primary] < Fixed.FromInt(100)); // enemy target hit
            Assert.True(w.Health[enemy]   < Fixed.FromInt(100)); // other enemy in the blast hit
        }
    }
}
