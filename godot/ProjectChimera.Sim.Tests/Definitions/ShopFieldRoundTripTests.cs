#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.16 — the three shop building fields (<c>sells_items</c>/<c>shop_stock</c>/<c>shop_radius</c>) round-trip
    /// through <see cref="FactionWriter"/> byte-faithfully, are Structure-gated + dangling-stock-id-rejected by
    /// <see cref="UnitDefinitionValidator"/>, and omit at their defaults so existing (non-shop) units re-serialize
    /// unchanged. Mirrors the <c>revives_heroes</c> coverage.
    /// </summary>
    public class ShopFieldRoundTripTests
    {
        private static UnitDefinition Deserialize(string json) =>
            JsonSerializer.Deserialize<UnitDefinition>(json, FactionDefinition.JsonOptions)!;

        [Fact]
        public void ShopFields_RoundTripThroughFactionWriter()
        {
            var def = new UnitDefinition
            {
                Id = "shop", DisplayName = "Item Shop", Category = "Structure",
                SellsItems = true, ShopStock = new[] { "ring", "potion" }, ShopRadius = 12f,
            };
            string json = FactionWriter.SerializeUnitClean(def);
            UnitDefinition rt = Deserialize(json);

            Assert.True(rt.SellsItems);
            Assert.Equal(new[] { "ring", "potion" }, rt.ShopStock);
            Assert.Equal(12f, rt.ShopRadius);
        }

        [Fact]
        public void NonShopUnit_OmitsShopKeys()
        {
            var def = new UnitDefinition { Id = "grunt", Category = "Melee" };
            string json = FactionWriter.SerializeUnitClean(def);
            Assert.DoesNotContain("sells_items", json);
            Assert.DoesNotContain("shop_stock", json);
            Assert.DoesNotContain("shop_radius", json);
        }

        // ── Validator: Structure-gating + dangling-stock-id ──

        private static readonly ItemRegistry Items = new(new List<ItemDefinition>
        {
            new ItemDefinition { Id = "ring", Charges = 0 },
            new ItemDefinition { Id = "potion", Charges = 3, EffectGraph = null! }, // effect irrelevant to the ref check
        });

        private static UnitDefinition ValidStructure() => new UnitDefinition
        {
            Id = "shop", Category = "Structure",
            Hp = 100f, Speed = 0f, AttackDamage = 0f, AttackRange = 0f, AttackSpeed = 1f,
            DamageType = "Normal", ArmorType = "Unarmored", CostOre = 50, CostCrystal = 0, Supply = 0, VisionRange = 8f,
            SeparationPriority = "Normal",
        };

        private static UnitValidationResult Run(UnitDefinition def) =>
            new UnitDefinitionValidator().Validate(def, registry: null, behaviorRegistry: null, itemRegistry: Items, siblings: null);

        [Fact]
        public void SellsItems_OnNonStructure_IsRejected()
        {
            var def = ValidStructure(); def.Category = "Melee"; def.SellsItems = true;
            var r = Run(def);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "sells_items");
        }

        [Fact]
        public void ShopStock_OnStructure_WithKnownItems_IsValid()
        {
            var def = ValidStructure(); def.SellsItems = true; def.ShopStock = new[] { "ring", "potion" }; def.ShopRadius = 10f;
            var r = Run(def);
            Assert.True(r.Ok, r.Ok ? "" : string.Join(" | ", r.Errors.Select(e => e.Message)));
        }

        [Fact]
        public void ShopStock_WithDanglingId_IsRejected()
        {
            var def = ValidStructure(); def.SellsItems = true; def.ShopStock = new[] { "ring", "nonexistent" };
            var r = Run(def);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "shop_stock");
        }

        [Fact]
        public void NoItemRegistry_SkipsStockRefCheck()
        {
            var def = ValidStructure(); def.SellsItems = true; def.ShopStock = new[] { "whatever" };
            var r = new UnitDefinitionValidator().Validate(def, registry: null, siblings: null); // 4-arg → itemRegistry null
            Assert.True(r.Ok, r.Ok ? "" : string.Join(" | ", r.Errors.Select(e => e.Message)));
        }

        [Fact]
        public void ShopFields_OmittedDefaults_AddNoError()
        {
            var def = ValidStructure(); // no shop fields set
            var r = Run(def);
            Assert.True(r.Ok, r.Ok ? "" : string.Join(" | ", r.Errors.Select(e => e.Message)));
        }
    }
}
