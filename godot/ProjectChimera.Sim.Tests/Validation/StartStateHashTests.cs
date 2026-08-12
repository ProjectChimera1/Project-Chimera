#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 3.2 (AC2 / AC3) — <see cref="StartStateHash"/> is a NEW canonical FNV-64 over the full init state =
    /// the scenario content model seed (via <see cref="CanonicalModelHash"/>) PLUS the <see cref="HeroStore"/> rows
    /// folded ASCENDING BY <see cref="HeroId"/>. This file is the FIRST pin (an INDEPENDENTLY hand-computed FNV-64
    /// over the documented byte stream — the 1.1 anti-tautology rule, never re-running Compute against itself) plus
    /// the behavioral teeth: a changed hero (added / level / XP) or changed content MUST move the hash, identical
    /// input is byte-identical, and the fold is mint-order-independent (producer independence). The SECOND pin (a
    /// recorded value over a fixed hero-loaded fixture) lives in the Golden/ hero-start-state golden.
    /// </summary>
    public class StartStateHashTests
    {
        /// <summary>A small but non-trivial applied-scenario model (distinct primary sort keys → order-stable).</summary>
        private static ScenarioData BuildModel(float supplyBump = 0f) => new ScenarioData
        {
            Id = "cosmetic-ignored",
            DisplayName = "cosmetic-ignored",
            TerrainRef = "res://terrain.tres",
            MapBounds = 120f,
            WinCondition = WinCondition.EliminateAllUnits,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, StartCrystal = 50f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 150f, StartCrystal = 30f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[]
            {
                new ScenarioResourceNode { X = -20f, Z = -10f, Supply = 400f + supplyBump, Rate = 5f, MaxGatherers = 4 },
            },
        };

        /// <summary>A HeroStore with two heroes minted at fixed identity / level / XP (a stable fixture).</summary>
        private static HeroStore TwoHeroes()
        {
            var s = new HeroStore();
            s.Mint(new HeroId(1_000_000_007UL), entityId: 3, level: 4, xp: Fixed.FromInt(250));
            s.Mint(new HeroId(2_000_000_011UL), entityId: 8, level: 1, xp: Fixed.FromInt(0));
            return s;
        }

        // DW-725: version-NEUTRAL name (the form DW-194's name-hygiene guard tells authors to prefer). The old
        // `AlgoVersion_IsTwo` embedded the cardinal word and survived that guard only while AlgoVersion happened to
        // be 2 — the next StartStateHash bump would have made the NAME a lie while the assertion was corrected.
        [Fact]
        public void AlgoVersion_IsPinned() => Assert.Equal(2, StartStateHash.AlgoVersion); // Story 3.15: bumped 1→2 (inventory + placed items)

        [Fact]
        public void MinimalModel_NoHeroes_MatchesIndependentlyComputedFnv64()
        {
            var model = BuildModel();
            var heroes = new HeroStore(); // empty → no hero rows folded

            // Build the documented canonical byte stream INDEPENDENTLY of StartStateHash's MixInt/MixULong, then fold
            // it with a textbook FNV-64. Byte stream (fixed order): AlgoVersion, then the CanonicalModelHash content
            // SEED as low-32 THEN high-32 (the MixULong convention), then NO hero rows (empty store). This pins the
            // algorithm without a self-tautology (the seed itself is independently pinned by CanonicalModelHashTests).
            ulong seed = CanonicalModelHash.Compute(model);
            var buf = new List<byte>();
            AppendInt(buf, StartStateHash.AlgoVersion);              // AlgoVersion (= 2), mixed FIRST
            AppendInt(buf, (int)(seed & 0xFFFFFFFFUL));              // seed low 32
            AppendInt(buf, (int)(seed >> 32));                      // seed high 32
            AppendInt(buf, model.InventorySlotCount ?? HeroStore.INVENTORY_SLOTS); // Story 3.15 (v2): usable-slot cap
            ulong expected = IndependentFnv64(buf.ToArray());
            if (expected == 0UL) expected = 1UL;                     // mirror the documented 0 → 1 sentinel

            Assert.Equal(expected, StartStateHash.Compute(model, heroes));
        }

        [Fact]
        public void TwoHeroes_MatchIndependentlyComputedFnv64()
        {
            // Extends the anti-tautology pin to the HERO ROWS. The empty-store pin above covers only AlgoVersion+seed;
            // the hero-row byte layout was otherwise pinned ONLY by the self-recorded golden (a transposition would bake
            // in and pass). Rebuild the FULL documented byte stream INDEPENDENTLY of MixInt/MixULong — AlgoVersion, seed
            // low/high, then each row ASCENDING BY HeroId as {Id low, Id high, Level, Xp.Raw} — so a row-fold transposition
            // breaks THIS test, not just a golden.
            var model = BuildModel();
            var heroes = TwoHeroes(); // ids 1_000_000_007 (lvl4/xp250) then 2_000_000_011 (lvl1/xp0) — already ascending

            ulong seed = CanonicalModelHash.Compute(model);
            var buf = new List<byte>();
            AppendInt(buf, StartStateHash.AlgoVersion);
            AppendInt(buf, (int)(seed & 0xFFFFFFFFUL));
            AppendInt(buf, (int)(seed >> 32));
            AppendInt(buf, model.InventorySlotCount ?? HeroStore.INVENTORY_SLOTS); // Story 3.15 (v2): usable-slot cap
            AppendHero(buf, 1_000_000_007UL, level: 4, xpRaw: Fixed.FromInt(250).Raw);
            AppendHero(buf, 2_000_000_011UL, level: 1, xpRaw: Fixed.FromInt(0).Raw);
            ulong expected = IndependentFnv64(buf.ToArray());
            if (expected == 0UL) expected = 1UL;

            Assert.Equal(expected, StartStateHash.Compute(model, heroes));
        }

        [Fact]
        public void IdenticalInit_IsByteIdentical()
        {
            // Two independent computations from identical input agree — the determinism guarantee (AC2).
            Assert.Equal(StartStateHash.Compute(BuildModel(), TwoHeroes()),
                         StartStateHash.Compute(BuildModel(), TwoHeroes()));
        }

        [Fact]
        public void AddedHero_ChangesHash()
        {
            var model = BuildModel();
            ulong before = StartStateHash.Compute(model, TwoHeroes());

            HeroStore more = TwoHeroes();
            more.Mint(new HeroId(3_000_000_019UL), entityId: 12, level: 2, xp: Fixed.FromInt(40));
            Assert.NotEqual(before, StartStateHash.Compute(model, more)); // a changed roster diverges (teeth)
        }

        [Fact]
        public void ChangedLevel_ChangesHash()
        {
            var model = BuildModel();
            ulong before = StartStateHash.Compute(model, TwoHeroes());

            var leveled = new HeroStore();
            leveled.Mint(new HeroId(1_000_000_007UL), entityId: 3, level: 5 /* was 4 */, xp: Fixed.FromInt(250));
            leveled.Mint(new HeroId(2_000_000_011UL), entityId: 8, level: 1, xp: Fixed.FromInt(0));
            Assert.NotEqual(before, StartStateHash.Compute(model, leveled));
        }

        [Fact]
        public void ChangedXp_ChangesHash()
        {
            var model = BuildModel();
            ulong before = StartStateHash.Compute(model, TwoHeroes());

            var xpGained = new HeroStore();
            xpGained.Mint(new HeroId(1_000_000_007UL), entityId: 3, level: 4, xp: Fixed.FromInt(251) /* was 250 */);
            xpGained.Mint(new HeroId(2_000_000_011UL), entityId: 8, level: 1, xp: Fixed.FromInt(0));
            Assert.NotEqual(before, StartStateHash.Compute(model, xpGained));
        }

        [Fact]
        public void MintOrderIndependent_HashEqual()
        {
            // The SAME heroes minted in OPPOSITE orders (and with different, unfolded entity links) must hash EQUAL —
            // the ascending-identity fold's producer-independence (M2-local vs M5-server mint-order divergence).
            var model = BuildModel();

            var forward = new HeroStore();
            forward.Mint(new HeroId(1_000_000_007UL), entityId: 3, level: 4, xp: Fixed.FromInt(250));
            forward.Mint(new HeroId(2_000_000_011UL), entityId: 8, level: 1, xp: Fixed.FromInt(0));

            var reverse = new HeroStore();
            reverse.Mint(new HeroId(2_000_000_011UL), entityId: 99 /* different link */, level: 1, xp: Fixed.FromInt(0));
            reverse.Mint(new HeroId(1_000_000_007UL), entityId: 77 /* different link */, level: 4, xp: Fixed.FromInt(250));

            Assert.Equal(StartStateHash.Compute(model, forward), StartStateHash.Compute(model, reverse));
        }

        [Fact]
        public void EntityLinkOnly_DoesNotChangeHash()
        {
            // EntityId (the runtime entity link) is NOT canonical persisted state and is NOT folded — two stores that
            // differ ONLY in which entity embodies each hero must hash EQUAL (else the producers would falsely diverge).
            var model = BuildModel();
            var a = new HeroStore();
            a.Mint(new HeroId(1_000_000_007UL), entityId: 3, level: 4, xp: Fixed.FromInt(250));
            var b = new HeroStore();
            b.Mint(new HeroId(1_000_000_007UL), entityId: 999, level: 4, xp: Fixed.FromInt(250));
            Assert.Equal(StartStateHash.Compute(model, a), StartStateHash.Compute(model, b));
        }

        [Fact]
        public void ChangedContentSeed_ChangesHash()
        {
            // A real gameplay change in the scenario model (via the folded CanonicalModelHash seed) moves the hash.
            var heroes = TwoHeroes();
            Assert.NotEqual(StartStateHash.Compute(BuildModel(),               heroes),
                            StartStateHash.Compute(BuildModel(supplyBump: 100f), heroes));
        }

        [Fact]
        public void PlacedItem_ChangesHash()
        {
            // Story 3.15 (v2): a placed map-item folds into the start-state hash, so a mismatched item loadout is
            // rejectable at the handshake. Adding one placed item to an otherwise-identical model MUST move the hash.
            var heroes = TwoHeroes();
            var model = BuildModel();
            var withItem = BuildModel();
            withItem.Items = new[] { new ScenarioItem { ItemId = "ring_of_vigor", X = 5f, Z = -4f } };
            Assert.NotEqual(StartStateHash.Compute(model, heroes), StartStateHash.Compute(withItem, heroes));
        }

        [Fact]
        public void HeroInventoryContent_ChangesHash()
        {
            // Story 3.15 (v2): the per-hero inventory refs fold, so a hero carrying an item hashes differently from an
            // empty-handed one (a mismatched carried loadout is rejectable at the handshake).
            var model = BuildModel();
            var empty = TwoHeroes();
            var carrying = TwoHeroes();
            carrying.Inventory[0 * HeroStore.INVENTORY_SLOTS + 0] = 3; // a non-empty ref in the first hero-row slot
            Assert.NotEqual(StartStateHash.Compute(model, empty), StartStateHash.Compute(model, carrying));
        }

        [Fact]
        public void ChangedInventorySlotCount_ChangesHash()
        {
            // Story 3.15 (P4, v2): the per-scenario usable inventory-slot cap folds into the start-state hash — it is
            // sim-affecting (drives full-inventory pickup denial) and must be handshake-rejectable. Two models that
            // differ ONLY in inventory_slot_count MUST hash differently.
            var heroes = TwoHeroes();
            var a = BuildModel(); a.InventorySlotCount = HeroStore.INVENTORY_SLOTS;
            var b = BuildModel(); b.InventorySlotCount = 3;
            Assert.NotEqual(StartStateHash.Compute(a, heroes), StartStateHash.Compute(b, heroes));
        }

        [Fact]
        public void DiffersFromRawCanonicalHash()
        {
            // StartStateHash is a DISTINCT hash, not CanonicalModelHash re-labelled: even with an empty HeroStore the
            // value differs (AlgoVersion is mixed first + the seed is folded as two mixes). Guards D4-C ("a NEW hash").
            var model = BuildModel();
            Assert.NotEqual(CanonicalModelHash.Compute(model), StartStateHash.Compute(model, new HeroStore()));
        }

        [Fact]
        public void Hash_IsNeverZero()
        {
            Assert.NotEqual(0UL, StartStateHash.Compute(BuildModel(), TwoHeroes()));
            Assert.NotEqual(0UL, StartStateHash.Compute(new ScenarioData(), new HeroStore())); // even an empty/default init
        }

        // ── Independent FNV-64 reference (NOT the production MixInt/MixULong) — copied from CanonicalModelHashTests. ──

        private static ulong IndependentFnv64(byte[] bytes)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong h = offset;
            foreach (byte b in bytes) { h ^= b; h *= prime; }
            return h;
        }

        private static void AppendInt(List<byte> buf, int value)
        {
            uint v = (uint)value; // 4 little-endian bytes
            buf.Add((byte)(v & 0xFF));
            buf.Add((byte)((v >> 8) & 0xFF));
            buf.Add((byte)((v >> 16) & 0xFF));
            buf.Add((byte)((v >> 24) & 0xFF));
        }

        private static void AppendHero(List<byte> buf, ulong id, int level, int xpRaw)
        {
            AppendInt(buf, (int)(id & 0xFFFFFFFFUL)); // HeroId low 32
            AppendInt(buf, (int)(id >> 32));          // HeroId high 32
            AppendInt(buf, level);
            AppendInt(buf, xpRaw);
            // Story 3.15 (v2): the per-hero inventory refs. A hero minted without items (TwoHeroes) folds the
            // INVENTORY_SLOTS empty sentinels (-1) — the byte layout a mismatched transposition must break.
            for (int s = 0; s < HeroStore.INVENTORY_SLOTS; s++)
                AppendInt(buf, HeroStore.INVENTORY_EMPTY);
        }
    }
}
