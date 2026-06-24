#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 1.7 (AC3) — <see cref="CanonicalModelHash"/> is a canonical FNV-64 over the model: stable across
    /// array order and across distinct floats that quantize to the same <see cref="Fixed"/>, sensitive to any
    /// real gameplay change, and excluding cosmetic Id/DisplayName and (deferred) Triggers. The single pinned
    /// value is computed INDEPENDENTLY (a hand-rolled FNV-64 over the documented byte stream), never by
    /// re-running Compute against itself (the 1.1 anti-tautology rule).
    /// </summary>
    public class CanonicalModelHashTests
    {
        /// <summary>A non-trivial model; every collection element has a distinct PRIMARY sort key so OrderBy is
        /// deterministic regardless of input order.</summary>
        private static ScenarioData BuildModel(bool reversed, string id = "M", string displayName = "m")
        {
            var slots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 150f, BaseX =  45f, BaseZ = 0f },
            };
            var nodes = new[]
            {
                new ScenarioResourceNode { X = -20f, Z = -10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  20f, Z =  10f, Supply = 600f, Rate = 6f, MaxGatherers = 3 },
            };
            var buildings = new[]
            {
                new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true },
                new ScenarioBuilding { Type = "Barracks",      Slot = 1, X =  45f, Z = 5f, PreBuilt = false },
            };
            var units = new[]
            {
                new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z = -3f },
                new ScenarioUnit { UnitId = "archer", Slot = 1, X =  42f, Z =  3f },
            };
            if (reversed)
            {
                Array.Reverse(slots);
                Array.Reverse(nodes);
                Array.Reverse(buildings);
                Array.Reverse(units);
            }
            return new ScenarioData
            {
                Id = id,
                DisplayName = displayName,
                TerrainRef = "res://terrain.tres",
                MapBounds = 120f,
                WinCondition = WinCondition.EliminateAllUnits,
                PlayerSlots = slots,
                ResourceNodes = nodes,
                Buildings = buildings,
                Units = units,
            };
        }

        [Fact]
        public void AlgoVersion_IsTwo() => Assert.Equal(2, CanonicalModelHash.AlgoVersion);

        [Fact]
        public void ReorderedCollections_HashEqual()
        {
            // Same multiset of elements, arrays reversed → sort restores a canonical order → identical hash.
            Assert.Equal(CanonicalModelHash.Compute(BuildModel(false)),
                         CanonicalModelHash.Compute(BuildModel(true)));
        }

        [Fact]
        public void CosmeticIdAndDisplayName_DoNotChangeHash()
        {
            Assert.Equal(CanonicalModelHash.Compute(BuildModel(false, id: "alpha", displayName: "Alpha Map")),
                         CanonicalModelHash.Compute(BuildModel(false, id: "OMEGA", displayName: "Totally Different")));
        }

        [Fact]
        public void Triggers_AreExcludedFromHash()
        {
            // Trigger/effect canonicalization is deferred to Epic 7 (D5) — Triggers must not affect the hash today.
            var a = BuildModel(false);
            var b = BuildModel(false);
            a.Triggers = Array.Empty<TriggerDefinition>();
            b.Triggers = new[] { new TriggerDefinition { Name = "T1" }, new TriggerDefinition { Name = "T2" } };
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void ChangedGameplayValue_HashDiffers()
        {
            var baseModel = BuildModel(false);
            var changed = BuildModel(false);
            changed.ResourceNodes[0].Supply += 100f; // a real gameplay change
            Assert.NotEqual(CanonicalModelHash.Compute(baseModel), CanonicalModelHash.Compute(changed));
        }

        [Fact]
        public void DistinctFloatsThatQuantizeEqual_HashEqual()
        {
            var a = BuildModel(false);
            var b = BuildModel(false);
            float v = a.Buildings[1].X;            // 45f — sits exactly on a 16.16 quantum boundary
            float vPlusUlp = MathF.BitIncrement(v); // the very next representable float — genuinely different bits
            Assert.True(vPlusUlp != v);             // precondition: distinct floats
            // ...but both map to the same Fixed.Raw (the integer the sim actually uses), so the hash must match.
            Assert.Equal(Fixed.FromFloat(v).Raw, Fixed.FromFloat(vPlusUlp).Raw);
            b.Buildings[1].X = vPlusUlp;
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void Hash_IsNeverZero()
        {
            Assert.NotEqual(0UL, CanonicalModelHash.Compute(BuildModel(false)));
            Assert.NotEqual(0UL, CanonicalModelHash.Compute(new ScenarioData())); // even an empty/default model
        }

        [Fact]
        public void ToWire_FoldsTo32Bit_AndAppliesZeroSentinel()
        {
            Assert.Equal(1u, CanonicalModelHash.ToWire(0UL));             // 0 → 1 (never the fail-open value)
            Assert.Equal(1u, CanonicalModelHash.ToWire(0x1_0000_0001UL)); // (uint)(h ^ (h>>32)) truncates to 0 → sentinel → 1
            ulong h = CanonicalModelHash.Compute(BuildModel(false));
            Assert.NotEqual(0u, CanonicalModelHash.ToWire(h));
            Assert.Equal(CanonicalModelHash.ToWire(h), CanonicalModelHash.ToWire(h)); // stable
        }

        [Fact]
        public void MinimalModel_MatchesIndependentlyComputedFnv64()
        {
            // A tiny model with empty collections. Id/DisplayName are set but MUST be excluded by Compute.
            var model = new ScenarioData
            {
                Id = "ignored",
                DisplayName = "ignored",
                TerrainRef = "",
                MapBounds = 120f,
                WinCondition = WinCondition.DestroyAllBuildings,
            };

            // Build the documented canonical byte stream (D5 fixed order) INDEPENDENTLY of MixInt/MixStr, then
            // fold it with a textbook FNV-64. This pins the algorithm without a self-tautology.
            var buf = new List<byte>();
            AppendInt(buf, CanonicalModelHash.AlgoVersion);  // AlgoVersion (= 2)
            AppendInt(buf, Fixed.FromFloat(120f).Raw);       // MapBounds quantized (= 7,864,320)
            AppendStr(buf, "DestroyAllBuildings");           // WinCondition by NAME
            AppendStr(buf, "");                              // TerrainRef
            // no slots / nodes / buildings / units
            ulong expected = IndependentFnv64(buf.ToArray());
            if (expected == 0UL) expected = 1UL;             // mirror the documented 0 → 1 sentinel

            Assert.Equal(expected, CanonicalModelHash.Compute(model));
        }

        [Fact]
        public void Buildings_DifferingOnlyInPreBuilt_AreOrderStable()
        {
            // Two buildings identical on (Slot, Type, X, Z) but differing on PreBuilt — a FOLDED field. PreBuilt
            // must be part of the sort order, else array order leaks into the hash → false MP desync. [Review][Patch]
            static ScenarioData Make(bool reversed)
            {
                var buildings = new[]
                {
                    new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = 10f, Z = 10f, PreBuilt = true },
                    new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = 10f, Z = 10f, PreBuilt = false },
                };
                if (reversed) Array.Reverse(buildings);
                return new ScenarioData
                {
                    TerrainRef = "", MapBounds = 120f, WinCondition = WinCondition.DestroyAllBuildings,
                    PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json" } },
                    Buildings = buildings,
                };
            }
            Assert.Equal(CanonicalModelHash.Compute(Make(false)), CanonicalModelHash.Compute(Make(true)));
        }

        [Fact]
        public void PlayerSlots_SharingASlotValue_AreOrderStable()
        {
            // Two slots sharing Slot but differing on folded fields. The validator rejects duplicate slots, but in
            // shadow mode such a model still reaches Compute — the hash must not depend on array order. [Review][Patch]
            static ScenarioData Make(bool reversed)
            {
                var slots = new[]
                {
                    new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                    new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://b.json", StartOre = 150f, BaseX =  45f, BaseZ = 0f },
                };
                if (reversed) Array.Reverse(slots);
                return new ScenarioData
                {
                    TerrainRef = "", MapBounds = 120f, WinCondition = WinCondition.DestroyAllBuildings,
                    PlayerSlots = slots,
                };
            }
            Assert.Equal(CanonicalModelHash.Compute(Make(false)), CanonicalModelHash.Compute(Make(true)));
        }

        // ── Independent FNV-64 reference (NOT the production MixInt/MixStr) ──

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

        private static void AppendStr(List<byte> buf, string? s)
        {
            AppendInt(buf, s?.Length ?? -1); // length prefix
            if (s != null) buf.AddRange(Encoding.UTF8.GetBytes(s));
        }
    }
}
