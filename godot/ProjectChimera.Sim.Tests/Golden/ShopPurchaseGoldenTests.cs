#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 3.16 (AC) — the SHOP-PURCHASE golden. Drives <see cref="ShopPurchaseScenario"/> (a hero buying a stat item
    /// at a shop) and asserts two in-process runs are byte-identical, the sequence reproduces the committed golden on
    /// EVERY OS, and the sequence EVOLVES (the buy mint/spend is doing real work over the already-folded v12 stores — NO
    /// new SimChecksum/StartStateHash algo bump). Cross-platform safe (integer/Fixed, Player2 empty).
    /// </summary>
    public class ShopPurchaseGoldenTests
    {
        private const string GoldenFile = "shop-purchase-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "shop-purchase golden-checksum baseline (Story 3.16) — CROSS-PLATFORM SAFE (integer/Fixed only)",
            "Pins the SimChecksum (v12 — NO algo bump) sequence for ShopPurchaseScenario.Build() (a Player1 hero buying a " +
            "stat item at a sells_items shop, spending ore + minting into inventory; Player2 empty so the AI no-ops) stepped " +
            "via StepOnce at ChecksumInterval=1. All hashed fields integer/Fixed → byte-identical Win↔Linux.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~ShopPurchaseGolden`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        private static IReadOnlyList<GoldenChecksumReplay.Sample> Run(int ticks)
        {
            var (h, items, buildSys, shopId) = ShopPurchaseScenario.Build();
            var seq = new List<GoldenChecksumReplay.Sample>(ticks);
            h.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            var buy = new UnitOrder(shopId, UnitCommand.BuyItem,
                                    Fixed.FromRaw(ShopPurchaseScenario.RingStock), Fixed.FromRaw(ShopPurchaseScenario.HeroEntityId));

            for (int i = 0; i < ticks; i++)
            {
                if (i == ShopPurchaseScenario.BuyTick)
                    OrderApplier.Apply(h.World, in buy, Faction.Player1, buildings: buildSys, items: items);
                h.Host.StepOnce();
            }
            return seq;
        }

        [Fact]
        public void RunsTwiceInProcess_AreByteIdentical()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var a = Run(ShopPurchaseScenario.DefaultTicks);
            var b = Run(ShopPurchaseScenario.DefaultTicks);
            Assert.True(a.SequenceEqual(b), "Two in-process shop-purchase runs diverged — same-machine nondeterminism.");
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = Run(ShopPurchaseScenario.DefaultTicks);
            var golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            var div = GoldenChecksumReplay.CompareSequences(golden, seq);
            Assert.True(div is null, div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void Sequence_Evolves_NotVacuous()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var seq = Run(ShopPurchaseScenario.DefaultTicks);
            Assert.True(seq.Select(s => s.Hash).Distinct().Count() > 1,
                "shop-purchase sequence is constant — the buy mint/spend is not moving folded state (vacuous golden).");
        }

        [Fact]
        public void RecordShopPurchaseBaseline()
        {
            var seq = Run(ShopPurchaseScenario.DefaultTicks);
            var seq2 = Run(ShopPurchaseScenario.DefaultTicks);
            Assert.True(seq.SequenceEqual(seq2), "Refusing to record: two in-process runs diverged.");
            GoldenChecksumReplay.MaybeRecord(seq, GoldenFile, Header);
        }
    }
}
