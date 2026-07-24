#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;             // Fixed, HeroStore
using ProjectChimera.Core.Definitions; // ContentHash, MatchAgreementHash, ScenarioData, ...
using ProjectChimera.Combat;           // DamageTable
using ProjectChimera.Effects;          // DirectHpDeltaEffect
using ProjectChimera.Multiplayer;      // HandshakeGate
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 9.16 — the end-to-end COMPOSITION proof: real content → real <see cref="ContentHash"/> →
    /// <see cref="MatchAgreementHash"/> → <see cref="HandshakeGate.CheckStart"/>. The per-domain unit tests prove the
    /// fold moves; the HandshakeGate unit tests prove a nonzero mismatch blocks. This joins them: two content sets
    /// differing in EACH domain, computed the way MainScene does, must make the REAL gate BLOCK — no synthetic literal
    /// hashes. This is the actual fail-closed reject the story ships.
    /// </summary>
    public class ContentHandshakeCompositionTests
    {
        private static ScenarioData Model()
        {
            var slots = new ScenarioPlayerSlot[2];
            for (int i = 0; i < 2; i++)
                slots[i] = new ScenarioPlayerSlot { Slot = i, FactionJson = $"res://f{i}.json", StartOre = 200f, BaseX = -30f + i * 60f };
            return new ScenarioData
            {
                Id = "m", DisplayName = "m", TerrainRef = "", MapBounds = 120f,
                WinCondition = WinCondition.EliminateAllUnits, PlayerSlots = slots,
            };
        }

        private static HeroStore Heroes()
        {
            var s = new HeroStore();
            s.Mint(new HeroId(1_000_000_007UL), entityId: 3, level: 4, xp: Fixed.FromInt(250));
            return s;
        }

        private static List<FactionDefinition> Factions(float warriorDamage = 10f, int researchTicks = 300) => new()
        {
            new FactionDefinition
            {
                Id = "alpha",
                Units = new List<UnitDefinition> { new UnitDefinition { Id = "warrior", AttackDamage = warriorDamage } },
                Research = new List<ResearchDefinition>
                {
                    new ResearchDefinition { Id = "sharpen", Levels = new List<ResearchLevel> { new ResearchLevel { TimeTicks = researchTicks } } },
                },
            },
        };

        private static AbilityRegistry Abilities(int hpDelta = -25) => new AbilityRegistry(new List<AbilityDefinition>
        {
            new AbilityDefinition { Id = "smite", Targeting = "TargetUnit", EffectGraph = new DirectHpDeltaEffect(Fixed.FromInt(hpDelta)) },
        });

        private static ItemRegistry Items(int atkDelta = 5) => new ItemRegistry(new List<ItemDefinition>
        {
            new ItemDefinition { Id = "sword", AttackDamageDelta = Fixed.FromInt(atkDelta) },
        });

        private static DamageTable Damage(float normalUnarmored = 1.0f)
        {
            string nu = normalUnarmored.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return DamageTable.FromJson(
                "{ \"multipliers\": {" +
                "\"Normal\": {\"Unarmored\":" + nu + ",\"Light\":1.0,\"Medium\":0.75,\"Heavy\":0.5,\"Fortified\":0.35,\"Hero\":1.0}," +
                "\"Pierce\": {\"Unarmored\":1.5,\"Light\":1.0,\"Medium\":0.75,\"Heavy\":0.35,\"Fortified\":0.25,\"Hero\":1.0}," +
                "\"Siege\": {\"Unarmored\":0.5,\"Light\":0.5,\"Medium\":1.0,\"Heavy\":1.0,\"Fortified\":1.5,\"Hero\":1.0}," +
                "\"Magic\": {\"Unarmored\":1.0,\"Light\":1.0,\"Medium\":1.0,\"Heavy\":1.0,\"Fortified\":0.5,\"Hero\":1.0}," +
                "\"Hero\": {\"Unarmored\":1.0,\"Light\":1.0,\"Medium\":1.0,\"Heavy\":1.0,\"Fortified\":1.0,\"Hero\":1.0}" +
                "}}");
        }

        private static ulong Hash(List<FactionDefinition> f, AbilityRegistry a, ItemRegistry i, DamageTable d)
            => MatchAgreementHash.Compute(4, Model(), Heroes(), f, a, i, d);

        [Fact]
        public void IdenticalContent_ComposesToAnAllow()
        {
            ulong a = Hash(Factions(), Abilities(), Items(), Damage());
            ulong b = Hash(Factions(), Abilities(), Items(), Damage());
            Assert.Null(HandshakeGate.CheckStart(a, b)); // equal nonzero → start allowed
        }

        [Theory]
        [InlineData("unit-stat")]
        [InlineData("damage-cell")]
        [InlineData("ability-effect")]
        [InlineData("item")]
        [InlineData("research")]
        public void ContentMismatchInAnyDomain_ComposesToAGateBlock(string domain)
        {
            // Peer A = the baseline content; peer B = the baseline with ONE domain mutated. Both hashes are the real
            // ContentHash→MatchAgreementHash value; the real gate must BLOCK (fail-closed, pre-tick).
            ulong a = Hash(Factions(), Abilities(), Items(), Damage());
            ulong b = domain switch
            {
                "unit-stat"      => Hash(Factions(warriorDamage: 11f), Abilities(), Items(), Damage()),
                "damage-cell"    => Hash(Factions(), Abilities(), Items(), Damage(normalUnarmored: 1.25f)),
                "ability-effect" => Hash(Factions(), Abilities(hpDelta: -30), Items(), Damage()),
                "item"           => Hash(Factions(), Abilities(), Items(atkDelta: 6), Damage()),
                "research"       => Hash(Factions(researchTicks: 301), Abilities(), Items(), Damage()),
                _                => a,
            };

            Assert.NotEqual(0UL, a);
            Assert.NotEqual(0UL, b);
            string? block = HandshakeGate.CheckStart(a, b);
            Assert.NotNull(block);
            Assert.Contains("MISMATCH", block);
        }
    }
}
