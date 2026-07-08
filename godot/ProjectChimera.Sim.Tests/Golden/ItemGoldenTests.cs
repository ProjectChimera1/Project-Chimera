#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 3.15 (AC) — the ITEM / INVENTORY golden. Drives <see cref="ItemScenario"/> (a hero picking up a stat item
    /// and a consumable, using a charge, then dying and dropping) and asserts two in-process runs are byte-identical, the
    /// sequence reproduces the committed golden on EVERY OS, and the sequence EVOLVES (the item cycle is doing real work).
    /// Cross-platform safe (integer/Fixed, Player2 empty) → compared on both CI legs. Exercises the v12 ItemStore +
    /// per-hero inventory fold end-to-end.
    /// </summary>
    public class ItemGoldenTests
    {
        private const string GoldenFile = "item-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "item golden-checksum baseline (Story 3.15) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v12, ItemStore + per-hero inventory now mutating) sequence for ItemScenario.Build() " +
            "(a Player1 hero picking up a stat item + a consumable, using a charge, then dying and dropping the stat item; " +
            "Player2 empty so the AI no-ops) stepped via StepOnce at ChecksumInterval=1. All hashed fields integer/Fixed → " +
            "byte-identical Win↔Linux; compared on both CI legs.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~ItemGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        private static IReadOnlyList<GoldenChecksumReplay.Sample> Run(int ticks)
        {
            var (h, items) = ItemScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(ticks);
            h.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            var pickRing   = new UnitOrder(ItemScenario.HeroEntityId, UnitCommand.PickupItem, Fixed.FromRaw(ItemScenario.RingRef), Fixed.Zero);
            var pickPotion = new UnitOrder(ItemScenario.HeroEntityId, UnitCommand.PickupItem, Fixed.FromRaw(ItemScenario.PotionRef), Fixed.Zero);
            var usePotion  = new UnitOrder(ItemScenario.HeroEntityId, UnitCommand.UseItem, Fixed.FromRaw(1), Fixed.Zero); // slot 1 (ring is slot 0)

            for (int i = 0; i < ticks; i++)
            {
                if (i == ItemScenario.PickRingTick)
                    OrderApplier.Apply(h.World, in pickRing, Faction.Player1, items: items);
                if (i == ItemScenario.PickPotionTick)
                    OrderApplier.Apply(h.World, in pickPotion, Faction.Player1, items: items);
                if (i == ItemScenario.UsePotionTick)
                    OrderApplier.Apply(h.World, in usePotion, Faction.Player1, items: items);
                if (i == ItemScenario.DeathTick)
                    h.World.Destroy(ItemScenario.HeroEntityId); // drop-on-death
                h.Host.StepOnce();
            }
            return seq;
        }

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var a = Run(ItemScenario.DefaultTicks);
            var b = Run(ItemScenario.DefaultTicks);
            Assert.True(a.SequenceEqual(b), "Two in-process item runs diverged — same-machine nondeterminism in the item path.");
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = Run(ItemScenario.DefaultTicks);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = Run(ItemScenario.DefaultTicks);
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "item sequence is constant — the pickup/use/drop cycle is not moving folded state (vacuous golden).");
        }

        [Fact]
        public void RecordItemBaseline()
        {
            var seq = Run(ItemScenario.DefaultTicks);
            Assert.True(seq.Count >= ItemScenario.DefaultTicks, $"Expected >= {ItemScenario.DefaultTicks} samples, got {seq.Count}.");
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1, "item sequence is constant (vacuous golden).");
            var seq2 = Run(ItemScenario.DefaultTicks);
            Assert.True(seq.SequenceEqual(seq2), "Refusing to record: two in-process runs diverged.");
            Assert.True(
                GoldenChecksumReplay.ParseGolden(System.Text.Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(seq, Header))).SequenceEqual(seq),
                "Refusing to record: FormatGolden/ParseGolden do not round-trip.");
            bool wrote = GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Header);
            if (wrote) Assert.True(GoldenChecksumReplay.IsRecordMode);
        }
    }
}
