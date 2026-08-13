#nullable enable
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Core.Stats;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Persistence
{
    /// <summary>
    /// DW-997 — a RUNTIME-MINTED modifier must survive a save round-trip. Before the fix, capture THREW
    /// ("an active modifier/persistent descriptor is unreachable by the canonical effect-descriptor table") for
    /// every such descriptor, because the table is built by walking ability/item EFFECT GRAPHS and a minted
    /// descriptor appears in none of them — so no save could be taken while a unit carried a stat item, after any
    /// research completed, or after any hero levelled. These tests pin the by-value round trip AND the one shape
    /// that legitimately still fails closed (a period effect, which is an effect graph).
    /// </summary>
    public class MintedModifierSaveRoundTripTests
    {
        private static SimulationHost Host()
        {
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                new FactionDefinition(), new FactionDefinition());
            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return host;
        }

        private static SaveGameState RoundTrip(SimulationHost host)
        {
            var table = CanonicalEffectDescriptorTable.Build(null, null);
            SaveGameState captured = SaveGameState.CaptureFrom(host, table);
            // Through the real binary body, not just the in-memory object — the frame's read/write must mirror.
            using var ms = new System.IO.MemoryStream();
            using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                captured.WriteBody(w);
            ms.Position = 0;
            SaveGameState reloaded;
            using (var r = new System.IO.BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                reloaded = SaveGameState.ReadBody(r, "test");
            reloaded.Validate("test");
            return reloaded;
        }

        [Fact]
        public void ACarriedStatItemsMintedModifier_SavesAndRestores_WithItsStatsIntact()
        {
            var host = Host();
            EntityWorld w = host.World;
            int hero = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            // Exactly what a pickup mints (the shipped ring_of_vigor shape: +50 max health while carried).
            var def = new ItemDefinition { Id = "probe_ring", MaxHealthDelta = Fixed.FromInt(50) };
            Assert.True(ItemSystem.ApplyItemStatModifier(host.Modifiers, w, def, hero, itemRef: 7));
            Assert.Equal(Fixed.FromInt(150).Raw, w.EffectiveMaxHealth[hero].Raw); // the item is live

            SaveGameState reloaded = RoundTrip(host); // ← THREW before DW-997

            Assert.Single(reloaded.Modifiers);
            SaveGameState.ModifierEntry e = reloaded.Modifiers[0];
            Assert.Equal(SaveGameState.KindMintedModifier, e.Kind);
            Assert.Equal(ItemSystem.ItemModifierId(7), e.ModifierId);
            Assert.Equal(-1, e.MintedDurationTicks);                 // permanent while carried
            Assert.Equal((int)StackRule.Ignore, e.MintedStacking);
            Assert.Equal(new[] { (int)StatId.MaxHealth }, e.MintedStatIds);
            Assert.Equal(new[] { Fixed.FromInt(50).Raw }, e.MintedStatRaws);

            // Restore onto a FRESH host and prove the stat is really back (not just the bytes).
            var fresh = Host();
            int freshHero = fresh.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            Assert.Equal(hero, freshHero); // same slot, so the saved HostId lands on it
            reloaded.RestoreInto(fresh, CanonicalEffectDescriptorTable.Build(null, null), new FactionDefinition?[5]);
            fresh.StepOnce(); // the dirty flag RestoreSlot left → the first recompute

            Assert.Equal(1, fresh.Modifiers.CountAt(freshHero));
            Assert.Equal(Fixed.FromInt(150).Raw, fresh.World.EffectiveMaxHealth[freshHero].Raw);
        }

        [Fact]
        public void AMintedModifierCarryingEverySparseStat_RoundTripsValueIdentically()
        {
            var host = Host();
            EntityWorld w = host.World;
            int unit = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            // A multi-stat minted descriptor (the affix shape 15-24g will produce), including a 15-24a/b stat
            // that only exists in the sparse lane — the legacy four alone would not have proven the vector path.
            var vector = StatVocabulary.Canonicalize(new List<StatDelta>
            {
                new StatDelta(StatId.MaxHealth, Fixed.FromInt(25)),
                new StatDelta(StatId.AttackSpeed, Fixed.Half),
                new StatDelta(StatId.CritChance, Fixed.FromRaw(Fixed.ONE / 4)),
            });
            var mod = new Modifier(0x7A11_0001, durationTicks: -1, StackRule.Refresh, maxStacks: 1,
                vector, StatusFlags.None, periodEffect: null, periodTicks: 0);
            Assert.True(host.Modifiers.Apply(unit, mod, unit, Faction.Player1));

            SaveGameState reloaded = RoundTrip(host);
            SaveGameState.ModifierEntry e = reloaded.Modifiers[0];

            Assert.Equal(vector.Length, e.MintedStatIds.Length);
            for (int i = 0; i < vector.Length; i++)
            {
                Assert.Equal((int)vector[i].Stat, e.MintedStatIds[i]); // ascending StatId order preserved
                Assert.Equal(vector[i].Delta.Raw, e.MintedStatRaws[i]);
            }

            var fresh = Host();
            fresh.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            reloaded.RestoreInto(fresh, CanonicalEffectDescriptorTable.Build(null, null), new FactionDefinition?[5]);
            fresh.StepOnce();

            Assert.Equal(Fixed.FromInt(125).Raw, fresh.World.EffectiveMaxHealth[unit].Raw);              // +25 flat
            Assert.Equal(Fixed.FromRaw(Fixed.ONE / 4).Raw, fresh.World.EffectiveCritChance[unit].Raw);   // +0.25 chance
            Assert.Equal((Fixed.One + Fixed.Half).Raw, fresh.World.EffectiveAttackSpeedFactor[unit].Raw); // +50% → factor 1.5
        }

        [Fact]
        public void RestoredMintedStats_ReachTheirConsumerChannels()
        {
            // Split out of the round-trip test so the assertion reads plainly: every restored stat lands on its
            // own consumer channel, i.e. the rebuilt descriptor went through the SAME accumulate path as a live one.
            var host = Host();
            int unit = host.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            var mod = new Modifier(0x7A11_0002, -1, StackRule.Refresh, 1,
                StatVocabulary.Canonicalize(new List<StatDelta>
                {
                    new StatDelta(StatId.CritChance, Fixed.Half),
                    new StatDelta(StatId.HealthRegen, Fixed.FromInt(3)),
                }),
                StatusFlags.None, null, 0);
            host.Modifiers.Apply(unit, mod, unit, Faction.Player1);

            SaveGameState reloaded = RoundTrip(host);
            var fresh = Host();
            fresh.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            reloaded.RestoreInto(fresh, CanonicalEffectDescriptorTable.Build(null, null), new FactionDefinition?[5]);
            fresh.StepOnce();

            Assert.Equal(Fixed.Half.Raw, fresh.World.EffectiveCritChance[unit].Raw);
            Assert.Equal(Fixed.FromInt(3).Raw, fresh.World.EffectiveHealthRegen[unit].Raw);
        }

        [Fact]
        public void AMintedModifierWithAPeriodEffect_StillFailsClosed()
        {
            // The one shape a by-value payload cannot carry: a period effect is an effect GRAPH. No minter authors
            // one today, so this is a tripwire for a future minter rather than a reachable path — and it must stay
            // an explicit refusal rather than silently dropping the pulse on load.
            var host = Host();
            int unit = host.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            var withPeriod = new Modifier(0x7A11_0003, -1, StackRule.Refresh, 1,
                StatVocabulary.FromLegacyFour(Fixed.FromInt(5), Fixed.Zero, Fixed.Zero, Fixed.Zero),
                StatusFlags.None, new DirectHpDeltaEffect(Fixed.FromInt(-1)), periodTicks: 10);
            host.Modifiers.Apply(unit, withPeriod, unit, Faction.Player1);

            var ex = Assert.Throws<System.InvalidOperationException>(
                () => SaveGameState.CaptureFrom(host, CanonicalEffectDescriptorTable.Build(null, null)));
            Assert.Contains("period_effect", ex.Message);
        }

        [Fact]
        public void ACorruptMintedPayload_FailsClosedOnLoad()
        {
            var host = Host();
            int unit = host.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            var mod = new Modifier(0x7A11_0004, -1, StackRule.Refresh, 1,
                StatVocabulary.FromLegacyFour(Fixed.FromInt(5), Fixed.Zero, Fixed.Zero, Fixed.Zero),
                StatusFlags.None, null, 0);
            host.Modifiers.Apply(unit, mod, unit, Faction.Player1);

            SaveGameState s = RoundTrip(host);
            // A stat id outside THIS build's registry (content drift / tampering) must refuse the load rather than
            // silently landing the delta on whatever stat happens to occupy that index.
            s.Modifiers[0].MintedStatIds = new[] { StatVocabulary.Count + 5 };
            s.Modifiers[0].MintedStatRaws = new[] { 1234 };

            var fresh = Host();
            fresh.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            var ex = Assert.Throws<System.IO.InvalidDataException>(
                () => s.RestoreInto(fresh, CanonicalEffectDescriptorTable.Build(null, null), new FactionDefinition?[5]));
            Assert.Contains("outside this build's registry", ex.Message);
        }
    }
}
