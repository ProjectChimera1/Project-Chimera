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
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, StartCrystal = 50f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 150f, StartCrystal = 30f, BaseX =  45f, BaseZ = 0f },
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
        public void AlgoVersion_IsEleven() => Assert.Equal(11, CanonicalModelHash.AlgoVersion); // 7.5 re-land merge bumped 9→10 (custom-events registry + graph node-kind fold); Story 7.9: 10→11 (Button fold)

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
        public void TerrainRefPath_DoesNotChangeHash()
        {
            // Story 6.2: TerrainRef is a machine-specific LOCAL PATH — the author's map dir
            // (res://…/{stem}_terrain) vs. a friend's imported copy (res://…/{id}_terrain/ — different stem AND a
            // trailing slash) for the IDENTICAL logical map. The sculpted terrain CONTENT lives in separate .res
            // files, never in this model, so the ref string must be NEUTRALIZED in the fold — else the same map
            // authored locally vs. imported elsewhere would hash DIFFERENTLY and be false-positive-rejected at the
            // MP lobby handshake (LobbyUi.ScenarioHash) / desync StartStateHash. All four variants (incl. empty)
            // MUST hash EQUAL.
            var a = BuildModel(false); a.TerrainRef = "res://resources/data/scenarios/my_map_terrain";
            var b = BuildModel(false); b.TerrainRef = "res://resources/data/scenarios/my_map_terrain/";
            var c = BuildModel(false); c.TerrainRef = "res://resources/data/scenarios/imported-123_terrain/";
            var d = BuildModel(false); d.TerrainRef = "";
            ulong ha = CanonicalModelHash.Compute(a);
            Assert.Equal(ha, CanonicalModelHash.Compute(b));
            Assert.Equal(ha, CanonicalModelHash.Compute(c));
            Assert.Equal(ha, CanonicalModelHash.Compute(d));
        }

        [Fact]
        public void EmptyTerrainRef_BaselineUnchanged()
        {
            // The neutralization is golden-preserving: because every existing scenario ships TerrainRef=="", mixing
            // a fixed "" is byte-identical to the pre-change fold. MinimalModel_MatchesIndependentlyComputedFnv64
            // (which folds "" for TerrainRef) is the independent-FNV proof the empty-ref baseline did not move;
            // this asserts the same value is stable and non-zero. AlgoVersion stays 5 — NO shipped-scenario golden
            // re-baseline.
            var model = new ScenarioData
            {
                TerrainRef = "", MapBounds = 120f, WinCondition = WinCondition.DestroyAllBuildings,
            };
            ulong once = CanonicalModelHash.Compute(model);
            Assert.Equal(once, CanonicalModelHash.Compute(model));
            Assert.NotEqual(0UL, once);
        }

        [Fact]
        public void Triggers_AreFoldedIntoHash() // Story 7.7 inverted the exclusion: triggers are sim-semantic
        {
            var a = BuildModel(false);
            var b = BuildModel(false);
            a.Triggers = Array.Empty<TriggerDefinition>();
            b.Triggers = new[] { new TriggerDefinition { Name = "T1" }, new TriggerDefinition { Name = "T2" } };
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void TriggerParamChange_MovesHash() // v8 sensitivity: a single trigger action field is semantic
        {
            static ScenarioData WithTrigger(int amountRaw)
            {
                var m = BuildModel(false);
                m.Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "reward",
                        Events = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Actions = new[] { new TriggerAction { Type = "add_resources", Faction = 0, Amount = Fixed.FromRaw(amountRaw) } },
                    },
                };
                return m;
            }
            Assert.NotEqual(CanonicalModelHash.Compute(WithTrigger(100 << 16)),
                            CanonicalModelHash.Compute(WithTrigger(200 << 16)));
        }

        [Fact]
        public void TriggerDeclarationOrder_IsSemantic_MovesHash()
        {
            // Declaration order breaks priority ties in the execution total order, so v8 deliberately folds
            // triggers IN ORDER — two scenarios with the same triggers in a different order hash differently.
            var t1 = new TriggerDefinition { Name = "A" };
            var t2 = new TriggerDefinition { Name = "B" };
            var a = BuildModel(false); a.Triggers = new[] { t1, t2 };
            var b = BuildModel(false); b.Triggers = new[] { t2, t1 };
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
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
        public void ChangedStartCrystal_HashDiffers()
        {
            // StartCrystal is sim-affecting (Crystal is folded into SimChecksum, and alpha_map_01.json now ships a
            // nonzero start_crystal), so two models differing ONLY in a slot's start_crystal MUST hash differently —
            // else the lobby start-state handshake would compare EQUAL and the match then desyncs in-sim from tick 1.
            // Teeth for the Story-2.9b-follow-up fold (AlgoVersion 2→3); this test would be RED against v2. [gds-code-review]
            var baseModel = BuildModel(false);
            var changed = BuildModel(false);
            changed.PlayerSlots[0].StartCrystal += 25f; // a real start-state change on exactly one slot
            Assert.NotEqual(CanonicalModelHash.Compute(baseModel), CanonicalModelHash.Compute(changed));
        }

        [Fact]
        public void ChangedSupplyConfig_HashDiffers()
        {
            // Supply is sim-affecting (folds into SimChecksum via SupplyCap/SupplyUsed, gates TrainUnit) — two
            // models differing ONLY in Supply MUST hash differently, else the lobby start-state handshake would
            // compare EQUAL and the match then desyncs in-sim from tick 1. Teeth for the Story 4.4 fold
            // (AlgoVersion 3→4); this test would be RED against v3.
            var baseModel = BuildModel(false);
            var changed = BuildModel(false);
            changed.Supply = new SupplyConfig { StartingCap = 10, HardCeiling = 50, Enabled = true };
            Assert.NotEqual(CanonicalModelHash.Compute(baseModel), CanonicalModelHash.Compute(changed));
        }

        [Fact]
        public void NullSupply_And_ExplicitAllDefaultSupply_HashEqual()
        {
            // Folding the RESOLVED value (not the raw nullable field) means an omitted `supply` block and an
            // explicitly-authored all-default SupplyConfig must hash IDENTICALLY — else every existing scenario
            // (which omits `supply`) would be a false-positive lobby mismatch against a creator who explicitly
            // authors the defaults.
            var withNull = BuildModel(false);
            withNull.Supply = null;
            var withDefault = BuildModel(false);
            withDefault.Supply = new SupplyConfig
            {
                StartingCap = ResourceStore.STARTING_SUPPLY_CAP,
                HardCeiling = null,
                Enabled = true,
            };
            Assert.Equal(CanonicalModelHash.Compute(withNull), CanonicalModelHash.Compute(withDefault));
        }

        [Fact]
        public void InvalidNegativeHardCeiling_HashDiffersFromOmittedSupply()
        {
            // Review-pass-2 regression: a naive `HardCeiling ?? -1` sentinel would make an authored (invalid,
            // shadow-mode-reachable) hard_ceiling=-1 hash IDENTICALLY to an omitted `supply` block (both folding
            // as -1), even though they resolve to materially different runtime behavior (uncapped vs. a clamped-
            // to-0 ceiling) — the exact false-negative this fold exists to prevent. Presence is now folded as an
            // explicit bit before the value, so these must hash DIFFERENTLY.
            var omitted = BuildModel(false);
            omitted.Supply = null;
            var invalidCeiling = BuildModel(false);
            invalidCeiling.Supply = new SupplyConfig { HardCeiling = -1 };
            Assert.NotEqual(CanonicalModelHash.Compute(omitted), CanonicalModelHash.Compute(invalidCeiling));
        }

        [Fact]
        public void NegativeAuthoredValues_ResolveClampedToZero_MatchingRuntimeResolution()
        {
            // SupplyConfig.Resolve (called identically by ResourceStore.ConfigureSupply and this Compute fold)
            // clamps a shadow-mode-reachable negative StartingCap/HardCeiling to 0 — so two DIFFERENT negative
            // authored values that resolve to the SAME clamped runtime state must hash IDENTICALLY (matching what
            // ConfigureSupply would actually apply), not differently by their raw un-clamped magnitude.
            var a = BuildModel(false);
            a.Supply = new SupplyConfig { StartingCap = -1, HardCeiling = -1, Enabled = true };
            var b = BuildModel(false);
            b.Supply = new SupplyConfig { StartingCap = -100, HardCeiling = -100, Enabled = true };
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));

            var (startingCap, hardCeiling, _) = SupplyConfig.Resolve(a.Supply);
            Assert.Equal(0, startingCap);
            Assert.Equal(0, hardCeiling);
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
            AppendInt(buf, CanonicalModelHash.AlgoVersion);  // AlgoVersion (= 11)
            AppendInt(buf, Fixed.FromFloat(120f).Raw);       // MapBounds quantized (= 7,864,320)
            AppendStr(buf, "DestroyAllBuildings");           // WinCondition by NAME
            AppendStr(buf, "");                              // TerrainRef
            // Story 6.5 (v6): pathability fold — absent paint + slope-off ⇒ digest 0, toggle 0, quantized threshold 0.
            AppendInt(buf, 0);                               // PathabilityBlocked == null → DigestOfBase64 == 0
            AppendInt(buf, 0);                               // SlopeAutoBlock == false → 0
            AppendInt(buf, Fixed.FromFloat(0f).Raw);         // SlopeBlockThreshold == 0f quantized (= 0)
            // Story 6.6 (v7): blocking-prop + water footprint digest — no props/water ⇒ empty footprint mask ⇒ 0.
            AppendInt(buf, 0);                               // BlockingFootprintDigest(null, null) == 0
            AppendInt(buf, ResourceStore.STARTING_SUPPLY_CAP); // Supply == null → resolved StartingCap default (10)
            AppendInt(buf, 0);                                 // Supply == null → resolved HardCeiling presence bit (absent → 0)
            AppendInt(buf, 0);                                 // Supply == null → resolved HardCeiling value (ignored when absent → 0)
            AppendInt(buf, 1);                                 // Supply == null → resolved Enabled default (true → 1)
            // no slots / nodes / buildings / units
            // Story 7.7 (v8): the trigger/DSL model — every collection folds a count prefix; all empty/absent here.
            AppendInt(buf, 0);                                 // Regions: null → count 0
            AppendInt(buf, 0);                                 // Triggers: default empty array → count 0
            AppendInt(buf, 0);                                 // Variables: null → count 0
            AppendInt(buf, 0);                                 // Timers: null → count 0
            AppendInt(buf, 0);                                 // TriggerGraphJson: absent → 0 marker
            // Story 7.8 (v9): the custom-UI widget tree — absent custom_ui folds a single 0 marker.
            AppendInt(buf, 0);                                 // CustomUi: absent → 0 marker
            // Story 7.5 via merge (v10): the custom-event registry — absent custom_events folds a 0 count.
            AppendInt(buf, 0);                                 // CustomEvents: null ≡ empty → count 0
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

        // ── Story 4.7: the 6 new ScenarioResourceNode fields ──────────────────────────────────────────────

        [Fact]
        public void DefaultOmittedNodeFields_MatchExplicitDefaults_HashEqual()
        {
            // "An all-default-omitted node hashes identically to pre-story content" — a node that never sets the
            // 6 new fields must hash IDENTICALLY to one that explicitly authors them at their documented defaults.
            var omitted = BuildModel(false); // BuildModel's nodes never touch the 6 new fields
            var explicitDefault = BuildModel(false);
            foreach (var n in explicitDefault.ResourceNodes)
            {
                n.CollectionModel = "Gather";
                n.ResourceType = "Ore";
                n.RequiresStructure = null;
                n.RequiresStructureRadius = 15f;
                n.OwnerSlot = -1;
                n.IncomePeriodTicks = 30;
            }
            Assert.Equal(CanonicalModelHash.Compute(omitted), CanonicalModelHash.Compute(explicitDefault));
        }

        [Fact]
        public void ChangedCollectionModel_HashDiffers()
        {
            var baseModel = BuildModel(false);
            var changed = BuildModel(false);
            changed.ResourceNodes[0].CollectionModel = "Streaming";
            Assert.NotEqual(CanonicalModelHash.Compute(baseModel), CanonicalModelHash.Compute(changed));
        }

        [Fact]
        public void ChangedResourceType_HashDiffers()
        {
            var baseModel = BuildModel(false);
            var changed = BuildModel(false);
            changed.ResourceNodes[0].ResourceType = "Crystal";
            Assert.NotEqual(CanonicalModelHash.Compute(baseModel), CanonicalModelHash.Compute(changed));
        }

        [Fact]
        public void ChangedRequiresStructure_HashDiffers_NullVsNonNull()
        {
            var baseModel = BuildModel(false); // RequiresStructure stays null
            var changed = BuildModel(false);
            changed.ResourceNodes[0].RequiresStructure = "watchtower";
            Assert.NotEqual(CanonicalModelHash.Compute(baseModel), CanonicalModelHash.Compute(changed));
        }

        [Fact]
        public void RequiresStructure_NullAndEmptyString_HashIdentically()
        {
            // Review patch: ScenarioApplier already normalizes "" -> null ("no gate" either way) — the hash must
            // agree, or two behaviorally-identical scenarios would false-positive-mismatch at the lobby handshake.
            var nullModel = BuildModel(false);
            var emptyModel = BuildModel(false);
            emptyModel.ResourceNodes[0].RequiresStructure = "";
            Assert.Equal(CanonicalModelHash.Compute(nullModel), CanonicalModelHash.Compute(emptyModel));
        }

        [Fact]
        public void ChangedRequiresStructureRadius_HashDiffers()
        {
            var baseModel = BuildModel(false);
            var changed = BuildModel(false);
            changed.ResourceNodes[0].RequiresStructureRadius += 5f;
            Assert.NotEqual(CanonicalModelHash.Compute(baseModel), CanonicalModelHash.Compute(changed));
        }

        [Fact]
        public void ChangedOwnerSlot_HashDiffers()
        {
            var baseModel = BuildModel(false);
            var changed = BuildModel(false);
            changed.ResourceNodes[0].OwnerSlot = 0;
            Assert.NotEqual(CanonicalModelHash.Compute(baseModel), CanonicalModelHash.Compute(changed));
        }

        [Fact]
        public void ChangedIncomePeriodTicks_HashDiffers()
        {
            var baseModel = BuildModel(false);
            var changed = BuildModel(false);
            changed.ResourceNodes[0].IncomePeriodTicks += 10;
            Assert.NotEqual(CanonicalModelHash.Compute(baseModel), CanonicalModelHash.Compute(changed));
        }

        [Theory]
        [InlineData("collection_model")]
        [InlineData("resource_type")]
        [InlineData("requires_structure")]
        [InlineData("requires_structure_radius")]
        [InlineData("owner_slot")]
        [InlineData("income_period_ticks")]
        public void ResourceNodes_DifferingOnlyInOneStory4_7Field_AreOrderStable(string field)
        {
            // Verification-gap patch: the node sort key must extend to a TOTAL order over EVERY one of the 6 new
            // fields, or array order leaks into the handshake hash (the class-doc requirement). The prior test
            // pinned only CollectionModel — parameterize across all six so a dropped later ThenBy is caught. Two
            // nodes identical on all pre-4.7 keys AND every other new field, differing only in `field`: if that
            // field participates in the sort they order deterministically (reversed input → same hash); if it were
            // missing from the sort they tie on all keys, the stable OrderBy preserves input order, and the
            // reversed array would hash differently — failing this test.
            static void SetField(ScenarioResourceNode n, string f, bool high)
            {
                switch (f)
                {
                    case "collection_model":          n.CollectionModel = high ? "Streaming" : "Gather"; break;
                    case "resource_type":             n.ResourceType = high ? "Crystal" : "Ore"; break;
                    case "requires_structure":        n.RequiresStructure = high ? "watchtower" : "armory"; break;
                    case "requires_structure_radius": n.RequiresStructureRadius = high ? 20f : 10f; break;
                    case "owner_slot":                n.OwnerSlot = high ? 0 : -1; break;
                    case "income_period_ticks":       n.IncomePeriodTicks = high ? 40 : 20; break;
                }
            }
            ScenarioData Make(bool reversed)
            {
                var a = new ScenarioResourceNode { X = 10f, Z = 10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 };
                var b = new ScenarioResourceNode { X = 10f, Z = 10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 };
                SetField(a, field, false);
                SetField(b, field, true);
                var nodes = new[] { a, b };
                if (reversed) System.Array.Reverse(nodes);
                return new ScenarioData
                {
                    TerrainRef = "", MapBounds = 120f, WinCondition = WinCondition.DestroyAllBuildings,
                    PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json" } },
                    ResourceNodes = nodes,
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
