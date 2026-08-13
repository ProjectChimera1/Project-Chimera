#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Stats;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Stats
{
    /// <summary>
    /// Story 15-24a — the REGISTRY COMPLETENESS guard: the invariants that make the StatVocabulary safe to
    /// grow by one entry (THE ADD-A-STAT RECIPE's rails). This is the closure of the enum-indexed-array
    /// defect class for stats: a <see cref="StatId"/> member that skips the registry, a duplicated JsonName,
    /// a percent stat pairing a non-value target, or a registry row whose index drifts from its id all fail
    /// HERE, at declaration time — never as a mis-indexed lane in a match.
    /// </summary>
    public class StatVocabularyGuardTests
    {
        [Fact]
        public void EveryStatId_HasExactlyOneRegistryRow_AtItsOwnIndex()
        {
            var members = (StatId[])Enum.GetValues(typeof(StatId));
            Assert.Equal(members.Length, StatVocabulary.Count); // one row per enum member, no extras
            foreach (StatId id in members)
                Assert.Equal(id, StatVocabulary.All[(int)id].Id); // index IS the id (Get()'s contract)
        }

        [Fact]
        public void StatIdValues_AreDenseFromZero_AppendOnly()
        {
            // The values enter saves (stride lanes, research blocks) and the canonical content fold, so they
            // must stay dense-from-zero: a gap would silently mis-stride every StatVocabulary.Count consumer.
            var seen = new HashSet<int>();
            foreach (StatId id in Enum.GetValues(typeof(StatId))) seen.Add((int)id);
            for (int i = 0; i < StatVocabulary.Count; i++)
                Assert.Contains(i, seen);
        }

        [Fact]
        public void JsonNames_AreUnique_NonEmpty_SnakeCase()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var def in StatVocabulary.All)
            {
                Assert.False(string.IsNullOrEmpty(def.JsonName));
                Assert.True(seen.Add(def.JsonName), $"duplicate JsonName '{def.JsonName}'");
                foreach (char c in def.JsonName)
                    Assert.True(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_',
                        $"'{def.JsonName}' is not snake_case (authoring token discipline)");
                Assert.True(StatVocabulary.TryByJsonName(def.JsonName, out var roundTrip) && roundTrip.Id == def.Id);
            }
            Assert.False(StatVocabulary.TryByJsonName("not_a_stat", out _));
            Assert.False(StatVocabulary.TryByJsonName(null, out _));
        }

        [Fact]
        public void PercentTargets_PointAtFlatValueStats_AndOnlyPercentStatsCarryThem()
        {
            foreach (var def in StatVocabulary.All)
            {
                if (def.PercentTarget is StatId target)
                {
                    Assert.Equal(StatAggregation.Percent, def.Aggregation); // pairing is a Percent-stat concept
                    var t = StatVocabulary.Get(target);
                    Assert.Equal(StatAggregation.Flat, t.Aggregation); // the recompute multiplies a VALUE stat
                    Assert.NotEqual(def.Id, target);
                }
            }
        }

        [Fact]
        public void EffectiveBounds_AreOrdered_AndLegacyStatsKeepTheExactZeroFloor()
        {
            foreach (var def in StatVocabulary.All)
                Assert.True(def.EffectiveMinRaw <= def.EffectiveMaxRaw, $"{def.JsonName}: min > max");

            // The legacy four's bounds ARE the Story 2.2b zero-floor — the bit-exactness of the generalized
            // recompute for pre-15-24a content rests on these exact values (max(0, x) ≡ clamp(x, 0, MaxValue)).
            foreach (StatId id in new[] { StatId.MaxHealth, StatId.AttackDamage, StatId.Armor, StatId.MoveSpeed })
            {
                Assert.Equal(0, StatVocabulary.Get(id).EffectiveMinRaw);
                Assert.Equal(int.MaxValue, StatVocabulary.Get(id).EffectiveMaxRaw);
                // …and no NEW per-delta cap: a tighter cap would reject previously-valid shipped content.
                Assert.Equal(0, StatVocabulary.Get(id).MaxAbsDeltaRaw);
            }
        }

        [Fact]
        public void Count_FitsTheConverterDuplicateBitmask()
        {
            // EffectNodeJsonConverter's stat_deltas duplicate rejection uses a 64-bit StatId bitmask.
            Assert.True(StatVocabulary.Count <= 64,
                "StatVocabulary grew past 64 — widen EffectNodeJsonConverter.ReadStatDeltasLane's `seen` bitmask first.");
        }

        [Fact]
        public void AuthoringFieldNames_KeepTheLegacyFlatKeys_AndRouteNewStatsThroughTheSparseLane()
        {
            Assert.Equal("max_health_delta", Modifier.AuthoringFieldName(StatId.MaxHealth));
            Assert.Equal("attack_damage_delta", Modifier.AuthoringFieldName(StatId.AttackDamage));
            Assert.Equal("move_speed_delta", Modifier.AuthoringFieldName(StatId.MoveSpeed));
            Assert.Equal("armor_delta", Modifier.AuthoringFieldName(StatId.Armor));
            Assert.Equal("stat_deltas.attack_speed", Modifier.AuthoringFieldName(StatId.AttackSpeed));
        }

        [Fact]
        public void Canonicalize_SortsMergesAndDropsZeros_AndCompatCtorMatchesVectorCtor()
        {
            var scratch = new List<StatDelta>
            {
                new StatDelta(StatId.MoveSpeed, Fixed.FromInt(2)),
                new StatDelta(StatId.MaxHealth, Fixed.FromInt(5)),
                new StatDelta(StatId.MoveSpeed, Fixed.FromInt(3)),   // merges with the first MoveSpeed
                new StatDelta(StatId.Armor, Fixed.Zero),             // dropped
            };
            var canon = StatVocabulary.Canonicalize(scratch);
            Assert.Equal(2, canon.Length);
            Assert.Equal(StatId.MaxHealth, canon[0].Stat); // ascending id
            Assert.Equal(Fixed.FromInt(5).Raw, canon[0].Delta.Raw);
            Assert.Equal(StatId.MoveSpeed, canon[1].Stat);
            Assert.Equal(Fixed.FromInt(5).Raw, canon[1].Delta.Raw); // 2 + 3 merged

            // The compat 12-arg ctor and the vector ctor must mint value-identical descriptors.
            var viaLegacy = new Modifier(7, -1, StackRule.Refresh, 1,
                Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5), StatusFlags.None, null, 0);
            var viaVector = new Modifier(7, -1, StackRule.Refresh, 1, canon, StatusFlags.None, null, 0);
            Assert.Equal(viaVector.StatDeltas.Length, viaLegacy.StatDeltas.Length);
            for (int i = 0; i < viaVector.StatDeltas.Length; i++)
            {
                Assert.Equal(viaVector.StatDeltas[i].Stat, viaLegacy.StatDeltas[i].Stat);
                Assert.Equal(viaVector.StatDeltas[i].Delta.Raw, viaLegacy.StatDeltas[i].Delta.Raw);
            }
            // …and the derived projection fields agree with the vector (no state hides in them).
            Assert.Equal(Fixed.FromInt(5).Raw, viaVector.MaxHealthDelta.Raw);
            Assert.Equal(Fixed.FromInt(5).Raw, viaVector.MoveSpeedDelta.Raw);
            Assert.Equal(0, viaVector.AttackDamageDelta.Raw);
            Assert.Equal(0, viaVector.ArmorDelta.Raw);
        }

        [Fact]
        public void CheckAuthoringBounds_EnforcesRegistryPerDeltaCaps_AndModifierAuthorableGate()
        {
            // A percent-family delta past its registry cap rejects, field-named through the sparse lane.
            var tooBig = new Modifier(1, -1, StackRule.Refresh, 1,
                new[] { new StatDelta(StatId.AttackSpeed, Fixed.FromInt(9)) }, StatusFlags.None, null, 0);
            var reject = tooBig.CheckAuthoringBounds();
            Assert.NotNull(reject);
            Assert.Equal("stat_deltas.attack_speed", reject!.Value.Field);

            // A declared-but-not-modifier-authorable stat (the energy pair) fails closed with its own reason.
            var energy = new Modifier(1, -1, StackRule.Refresh, 1,
                new[] { new StatDelta(StatId.MaxEnergy, Fixed.FromInt(10)) }, StatusFlags.None, null, 0);
            var energyReject = energy.CheckAuthoringBounds();
            Assert.NotNull(energyReject);
            Assert.Contains("not modifier-authorable", energyReject!.Value.Reason);

            // A sane new-stat delta passes.
            var ok = new Modifier(1, -1, StackRule.Refresh, 1,
                new[] { new StatDelta(StatId.AttackSpeed, Fixed.Half) }, StatusFlags.None, null, 0);
            Assert.Null(ok.CheckAuthoringBounds());
        }
    }
}
